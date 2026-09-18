using System;
using System.Collections.Generic;
using static OsrsTouch.Native;

namespace OsrsTouch {

/// <summary>One finger on the screen, in screen pixels.</summary>
struct Contact
{
    public uint Id;
    public double X, Y;     // screen pixels, once the mapping has been applied
    public double U, V;     // the panel's own coordinates, 0..1 on each axis
    public Contact(uint id, double x, double y, double u, double v) { Id = id; X = x; Y = y; U = u; V = v; }
}

/// <summary>
/// Reads raw HID reports from the touchscreen (works in the background via RIDEV_INPUTSINK)
/// and turns them into complete "frames" listing every finger currently touching.
/// Handles both "parallel" (all fingers per report) and "hybrid" (fingers split across reports) digitizers.
/// </summary>
unsafe class TouchReader
{
    const ushort PAGE_DIGITIZER = 0x0D, PAGE_GENERIC = 0x01;
    const ushort USAGE_TIP = 0x42, USAGE_CONTACT_ID = 0x51, USAGE_CONTACT_COUNT = 0x54, USAGE_X = 0x30, USAGE_Y = 0x31;

    class Device
    {
        public byte[] Preparsed;
        public List<ushort> FingerLinks = new List<ushort>();
        public ushort CountLink; public bool HasCount;
        public int MaxX = 1, MaxY = 1;
        // hybrid-mode frame assembly
        public int Expected, Received;
        public bool Open;
        public List<Contact> Pending = new List<Contact>();
    }

    readonly Dictionary<IntPtr, Device> _devices = new Dictionary<IntPtr, Device>();
    public event Action<List<Contact>> Frame;
    public bool Verbose;
    public bool LogRaw;

    public static bool Register(IntPtr hwnd)
    {
        RAWINPUTDEVICE* devs = stackalloc RAWINPUTDEVICE[1];
        devs[0] = new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }; // touch screen
        
        if (RegisterRawInputDevices(devs, 1, (uint)sizeof(RAWINPUTDEVICE))) return true;
        return false;
    }

    public void OnWmInput(IntPtr hRaw)
    {
        uint headerSize = (uint)(sizeof(uint) * 2 + IntPtr.Size * 2);
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, null, ref size, headerSize);
        if (size == 0) return;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            if (GetRawInputData(hRaw, RID_INPUT, p, ref size, headerSize) == uint.MaxValue) return;
            uint type = BitConverter.ToUInt32(buf, 0);
            if (type != 2) return; // RIM_TYPEHID
            IntPtr hDev = (IntPtr)BitConverter.ToInt64(buf, 8);
            int off = (int)headerSize;
            uint sizeHid = BitConverter.ToUInt32(buf, off);
            uint count = BitConverter.ToUInt32(buf, off + 4);
            var dev = GetDevice(hDev);
            if (dev == null) return;
            for (uint i = 0; i < count; i++)
                ParseReport(dev, p + off + 8 + i * sizeHid, sizeHid);
        }
    }

    Device GetDevice(IntPtr hDev)
    {
        Device d;
        if (_devices.TryGetValue(hDev, out d)) return d;
        d = null;
        try
        {
            uint size = 0;
            GetRawInputDeviceInfoW(hDev, RIDI_PREPARSEDDATA, null, ref size);
            if (size == 0) return null;
            var pre = new byte[size];
            fixed (byte* pp = pre)
            {
                if (GetRawInputDeviceInfoW(hDev, RIDI_PREPARSEDDATA, pp, ref size) == uint.MaxValue) return null;
                byte* caps = stackalloc byte[64];
                if (HidP_GetCaps(pp, caps) != HIDP_STATUS_SUCCESS) return null;
                ushort nValue = (ushort)(caps[48] | (caps[49] << 8));
                var vc = new byte[Math.Max(1, (int)nValue) * 72];
                fixed (byte* pv = vc)
                {
                    ushort len = nValue;
                    if (HidP_GetValueCaps(0, pv, ref len, pp) != HIDP_STATUS_SUCCESS) return null;
                    d = new Device { Preparsed = pre };
                    for (int i = 0; i < len; i++)
                    {
                        int b = i * 72;
                        ushort page = BitConverter.ToUInt16(vc, b);
                        ushort link = BitConverter.ToUInt16(vc, b + 6);
                        bool isRange = vc[b + 12] != 0;
                        int logicalMax = BitConverter.ToInt32(vc, b + 44);
                        ushort uMin = BitConverter.ToUInt16(vc, b + 56);
                        ushort uMax = isRange ? BitConverter.ToUInt16(vc, b + 58) : uMin;
                        if (page == PAGE_GENERIC && (USAGE_X >= uMin && USAGE_X <= uMax))
                        {
                            if (!d.FingerLinks.Contains(link)) d.FingerLinks.Add(link);
                            if (logicalMax > 0) d.MaxX = Math.Max(d.MaxX, logicalMax);
                        }
                        if (page == PAGE_GENERIC && (USAGE_Y >= uMin && USAGE_Y <= uMax) && logicalMax > 0) d.MaxY = Math.Max(d.MaxY, logicalMax);
                        if (page == PAGE_DIGITIZER && (USAGE_CONTACT_COUNT >= uMin && USAGE_CONTACT_COUNT <= uMax)) { d.HasCount = true; d.CountLink = link; }
                    }
                    d.FingerLinks.Sort();
                }
            }
            Program.Log(string.Format("Touch device found: {0} finger slots, contact-count={1}, range {2}x{3}", d.FingerLinks.Count, d.HasCount ? "yes" : "no", d.MaxX, d.MaxY));
        }
        finally { _devices[hDev] = d; }
        return d;
    }

    // A "frame" is one snapshot of every finger on the glass. Panels deliver it two ways:
    //  - parallel: one report carries every finger
    //  - hybrid:   one report per finger (or per pair), the first one carrying the total contact count
    // So we collect contacts until we have as many as the contact count promised, then publish.
    void ParseReport(Device d, byte* report, uint len)
    {
        fixed (byte* pp = d.Preparsed)
        {
            uint cc = 0, x, y, id;
            bool haveCc = false;
            if (d.HasCount)
            {
                if (HidP_GetUsageValue(0, PAGE_DIGITIZER, d.CountLink, USAGE_CONTACT_COUNT, out cc, pp, report, len) != HIDP_STATUS_SUCCESS)
                    return; // not a touch report (some panels share the interface with other reports)
                haveCc = true;
            }

            if (!haveCc || cc > 0)
            {
                Flush(d);                       // a new frame starts here; publish whatever the last one had
                d.Expected = haveCc ? (int)cc : 0;
                d.Received = 0;
                d.Pending.Clear();
                d.Open = true;
            }

            double sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
            ushort* usages = stackalloc ushort[32];
            int found = 0;
            for (int i = 0; i < d.FingerLinks.Count; i++)
            {
                // The panel always describes every finger slot, whether or not a finger is in it.
                // The contact count says how many are real, so once we have that many we stop:
                // reading further slots picks up stale leftovers and invents extra fingers.
                if (d.Expected > 0 && d.Received + found >= d.Expected) break;

                ushort link = d.FingerLinks[i];
                if (HidP_GetUsageValue(0, PAGE_GENERIC, link, USAGE_X, out x, pp, report, len) != HIDP_STATUS_SUCCESS) continue;
                HidP_GetUsageValue(0, PAGE_GENERIC, link, USAGE_Y, out y, pp, report, len);
                HidP_GetUsageValue(0, PAGE_DIGITIZER, link, USAGE_CONTACT_ID, out id, pp, report, len);
                uint n = 32;
                bool tip = false;
                if (HidP_GetUsages(0, PAGE_DIGITIZER, link, usages, ref n, pp, report, len) == HIDP_STATUS_SUCCESS)
                    for (int k = 0; k < n; k++) if (usages[k] == USAGE_TIP) tip = true;

                if (!tip && id == 0 && x == 0 && y == 0) continue; // plainly empty slot
                found++;
                if (LogRaw) Program.Log(string.Format("    slot {0}: id={1} tip={2} x={3} y={4}", i, id, tip ? 1 : 0, x, y));
                if (!tip) continue;
                for (int k = d.Pending.Count - 1; k >= 0; k--) if (d.Pending[k].Id == id) d.Pending.RemoveAt(k);
                double u = (double)x / d.MaxX, v = (double)y / d.MaxY;
                d.Pending.Add(new Contact(id, u * sw, v * sh, u, v));
            }
            d.Received += found;
            if (LogRaw) Program.Log(string.Format("  report: count={0} slots-filled={1} have={2}/{3}", haveCc ? cc.ToString() : "-", found, d.Received, d.Expected));

            if (d.Expected == 0 || d.Received >= d.Expected) Flush(d);
        }
    }

    void Flush(Device d)
    {
        if (!d.Open) return;
        d.Open = false;
        var frame = new List<Contact>(d.Pending);
        d.Pending.Clear(); d.Received = 0; d.Expected = 0;
        if (Frame != null) Frame(frame);
    }
}
}
