using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace SimplySerial
{
    /// <summary>
    /// Custom structure containing the name, VID, PID and description of a serial (COM) port
    /// Modified from the example written by Kamil Górski (freakone) available at
    /// http://blog.gorski.pm/serial-port-details-in-c-sharp
    /// https://github.com/freakone/serial-reader
    /// </summary>
    public class ComPort // custom struct with our desired values
    {
        public string name;
        public int num = -1;
        public string vid = "----";
        public string pid = "----";
        public string description;
        public string busDescription;
        public DateTime lastArrival;
        public Board board;
        public bool isCircuitPython = false;
        public bool isStLink = false;
        public string stLinkSerial = "";
    }


    public class ComPortList
    {
        public List<ComPort> Available = new List<ComPort>();
        public List<ComPort> Excluded = new List<ComPort>();
    }


    public static class ComPortManager
    {
        public static FilterSet Filters = new FilterSet();

        /// <summary>
        /// Returns a list of available serial ports with their associated PID, VID and descriptions
        /// Modified from the example written by Kamil Górski (freakone) available at
        /// http://blog.gorski.pm/serial-port-details-in-c-sharp
        /// https://github.com/freakone/serial-reader
        /// Some modifications were based on this stackoverflow thread:
        /// https://stackoverflow.com/questions/11458835/finding-information-about-all-serial-devices-connected-through-usb-in-c-sharp
        /// Hardware Bus Description through WMI is based on Simon Mourier's answer on this stackoverflow thread:
        /// https://stackoverflow.com/questions/69362886/get-devpkey-device-busreporteddevicedesc-from-win32-pnpentity-in-c-sharp
        /// </summary>
        /// <returns>List of available serial ports</returns>
        public static ComPortList GetPorts()
        {
            const string vidPattern = @"VID_([0-9A-F]{4})";
            const string pidPattern = @"PID_([0-9A-F]{4})";
            const string namePattern = @"(?<=\()COM[0-9]{1,3}(?=\)$)";
            const string query = "SELECT * FROM Win32_PnPEntity WHERE ClassGuid=\"{4d36e978-e325-11ce-bfc1-08002be10318}\"";

            // as per INTERFACE_PREFIXES in adafruit_board_toolkit
            // (see https://github.com/adafruit/Adafruit_Board_Toolkit/blob/main/adafruit_board_toolkit)
            string[] cpb_descriptions = new string[] { "CircuitPython CDC ", "Sol CDC ", "StringCarM0Ex CDC " };

            // known ST-Link debugger PIDs (VID 0483 = STMicroelectronics)
            // PID assignments per ST's 99-stlink-plugdev.rules udev file
            HashSet<string> stlinkPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "3744", // ST-Link v1
                "3748", // ST-Link v2
                "374A", // ST-Link v2.1
                "374B", // ST-Link v2.1
                "3752", // ST-Link v2.1 / STLink V3SET in Dual CDC mode
                "3753", // STLink V3SET in Dual CDC mode
                "374D", // STLink V3SET
                "374E", // STLink V3SET
                "374F", // STLink V3SET in normal mode
                "3754", // STLink V3 (observed in the wild)
                "3755", // STLink V3-PWR
                "3757", // STLink V3-PWR
            };

            if (Filters.All == null)
            {
                Filters.All = Filter.AddFrom(SimplySerial.AppFolder + SimplySerial.FilterFile);
                if (SimplySerial.AppFolder != SimplySerial.WorkingFolder)
                {
                    Filters.All = Filter.AddFrom(SimplySerial.WorkingFolder + SimplySerial.FilterFile, existing: Filters.All);
                }
            }

            List<ComPort> detectedPorts = new List<ComPort>();

            foreach (var p in new ManagementObjectSearcher("root\\CIMV2", query).Get().OfType<ManagementObject>())
            {
                ComPort c = new ComPort();

                // extract and clean up port name and number
                c.name = p.GetPropertyValue("Name").ToString();
                Match mName = Regex.Match(c.name, namePattern);
                if (mName.Success)
                {
                    c.name = mName.Value;
                    c.num = int.Parse(c.name.Substring(3));
                }

                // if the port name or number cannot be determined, skip this port and move on
                if (c.num < 1)
                    continue;

                // get the device's VID and PID
                string pidvid = p.GetPropertyValue("PNPDeviceID").ToString();

                // extract and clean up device's VID
                Match mVID = Regex.Match(pidvid, vidPattern, RegexOptions.IgnoreCase);
                if (mVID.Success)
                    c.vid = mVID.Groups[1].Value.Substring(0, Math.Min(4, c.vid.Length));

                // extract and clean up device's PID
                Match mPID = Regex.Match(pidvid, pidPattern, RegexOptions.IgnoreCase);
                if (mPID.Success)
                    c.pid = mPID.Groups[1].Value.Substring(0, Math.Min(4, c.pid.Length));

                // extract the device's friendly description (caption)
                c.description = p.GetPropertyValue("Caption").ToString();

                // for FTDI devices the COM port's own caption is the generic "USB Serial Port (COMxx)".
                // The USB parent's BusReportedDeviceDesc carries the iProduct string from the EEPROM
                // (e.g. "Smart Meter"), which is far more useful than the generic driver caption
                // ("USB Serial Converter"). Fall back to the parent caption when no iProduct is set.
                if (c.vid == "0403")
                {
                    ManagementObject usbParent = FindUsbRootParent(p, pidvid);
                    if (usbParent != null)
                    {
                        string parentProduct = GetDeviceProperty(usbParent, "DEVPKEY_Device_BusReportedDeviceDesc");
                        if (!string.IsNullOrEmpty(parentProduct))
                        {
                            c.description = parentProduct;
                        }
                        else
                        {
                            try
                            {
                                string parentCaption = usbParent.GetPropertyValue("Caption")?.ToString();
                                if (!string.IsNullOrEmpty(parentCaption))
                                    c.description = parentCaption;
                            }
                            catch { }
                        }
                    }
                }

                // attempt to match this device with a known board
                c.board = BoardManager.Match(c.vid, c.pid);

                // extract the device's hardware bus description
                c.busDescription = "";
                try
                {
                    var inParams = new object[] { new string[] { "DEVPKEY_Device_BusReportedDeviceDesc" }, null };
                    p.InvokeMethod("GetDeviceProperties", inParams);
                    var outParams = (ManagementBaseObject[])inParams[1];
                    if (outParams.Length > 0)
                    {
                        var data = outParams[0].Properties.OfType<PropertyData>().FirstOrDefault(d => d.Name == "Data");
                        if (data != null)
                        {
                            c.busDescription = data.Value.ToString();
                        }
                    }
                } catch { }

                // extract the device's last arrival
                c.lastArrival = DateTime.MinValue;
                try
                {
                    var inParams = new object[] { new string[] { "DEVPKEY_Device_LastArrivalDate" }, null };
                    p.InvokeMethod("GetDeviceProperties", inParams);
                    var outParams = (ManagementBaseObject[])inParams[1];
                    if (outParams.Length > 0)
                    {
                        var data = outParams[0].Properties.OfType<PropertyData>().FirstOrDefault(d => d.Name == "Data");
                        if (data != null)
                        {
                            c.lastArrival = ManagementDateTimeConverter.ToDateTime(data.Value.ToString());
                        }
                    }
                }
                catch { }

                // we can determine if this is a CircuitPython board by its bus description
                foreach (string prefix in cpb_descriptions)
                {
                    if (c.busDescription.StartsWith(prefix))
                        c.isCircuitPython = true;
                }

                // detect ST-Link debugger virtual COM port
                // match by known VID/PID, or by STMicroelectronics VID + "STLink"/"ST-Link" in description
                // only flag as ST-Link if we can also resolve the probe's hardware serial number,
                // so SWD reset can unambiguously target the correct adapter (multi-probe safe).
                if (c.vid == "0483" &&
                    (stlinkPids.Contains(c.pid) ||
                     c.description.IndexOf("STLink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     c.description.IndexOf("ST-Link", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     c.busDescription.IndexOf("STLink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     c.busDescription.IndexOf("ST-Link", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    c.stLinkSerial = ResolveStLinkSerial(p, pidvid);
                    c.isStLink = !string.IsNullOrEmpty(c.stLinkSerial);
                }

                detectedPorts.Add(c);
            }

            // apply filters to determine if this port should be included or excluded in autodetection
            ComPortList ports = new ComPortList();

            // if there are *any* include filters than we can *only* include matches, and anything that doesn't match gets excluded
            if (Filters.Include.Count > 0)
            {
                foreach (ComPort p in detectedPorts)
                {
                    bool matched = false;

                    foreach (Filter f in Filters.Include)
                    {
                        if (Filter.MatchFilter(f, p))
                        {
                            ports.Available.Add(p);
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        ports.Excluded.Add(p);
                    }
                }
            }
            else
            {
                // if there are *no* include filters, then we start out including everything
                ports.Available = detectedPorts;
            }

            // once we have our initial include list, we apply our exclude filters to remove any ports that match and add them to the exclude list
            foreach (ComPort p in ports.Available.ToList())
            {
                foreach (Filter f in Filters.Exclude.Concat(Filters.Block))
                {
                    if (Filter.MatchFilter(f, p))
                    {
                        ports.Available.Remove(p);
                        ports.Excluded.Add(p);
                    }
                }
            }

            ports.Available = ports.Available.Distinct().OrderBy(p => p.num).ToList();
            ports.Excluded = ports.Excluded.Distinct().OrderBy(p => p.num).ToList();

            if (ports.Available.Count == 0 && Filters.Block.Count > 0)
            {
                Filters.All.RemoveAll(f => f.Type == FilterType.BLOCK);
            }

            return ports;
        }

        /// <summary>
        /// Invokes Win32_PnPEntity.GetDeviceProperties for a single DEVPKEY and returns the value as a string.
        /// Returns "" on any error or if the property is unset.
        /// </summary>
        private static string GetDeviceProperty(ManagementObject obj, string devPropKey)
        {
            try
            {
                var inParams = new object[] { new string[] { devPropKey }, null };
                obj.InvokeMethod("GetDeviceProperties", inParams);
                var outParams = (ManagementBaseObject[])inParams[1];
                if (outParams.Length == 0)
                    return "";
                var data = outParams[0].Properties.OfType<PropertyData>().FirstOrDefault(d => d.Name == "Data");
                if (data == null || data.Value == null)
                    return "";
                return data.Value.ToString();
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Walks the USB device parent chain from a child (e.g. a VCP/CDC interface) up to the
        /// root USB device — the instance whose PNPDeviceID looks like USB\VID_XXXX&PID_XXXX\<serial>
        /// and carries no &amp;MI_NN interface qualifier. Returns null if no such ancestor is found.
        /// </summary>
        private static ManagementObject FindUsbRootParent(ManagementObject portObj, string portInstanceId)
        {
            // root USB device PNPDeviceID looks like USB\VID_XXXX&PID_XXXX\<sn>.
            // Child interfaces are USB\VID_XXXX&PID_XXXX&MI_NN\<...> — the &MI_ token
            // before the final '\' disqualifies them. SNs themselves may contain '&'
            // (Windows fabricates instance IDs like "6&d382ae8&0&1" when no EEPROM SN exists).
            Regex rootRegex = new Regex(@"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\[^\\]+$", RegexOptions.IgnoreCase);

            try
            {
                string currentId = portInstanceId;
                ManagementObject currentObj = portObj;

                for (int hop = 0; hop < 6; hop++)
                {
                    if (rootRegex.IsMatch(currentId))
                        return currentObj;

                    string parentId = GetDeviceProperty(currentObj, "DEVPKEY_Device_Parent");
                    if (string.IsNullOrEmpty(parentId) || parentId == currentId)
                        break;

                    string escaped = parentId.Replace("\\", "\\\\").Replace("'", "''");
                    using (var search = new ManagementObjectSearcher("root\\CIMV2",
                        $"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID='{escaped}'"))
                    {
                        ManagementObject next = search.Get().OfType<ManagementObject>().FirstOrDefault();
                        if (next == null)
                            break;
                        currentObj = next;
                        currentId = parentId;
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Recovers the ST-Link probe's hardware serial number from the root USB device
        /// (used as STM32_Programmer_CLI's sn= argument when multiple ST-Link probes are connected).
        /// </summary>
        private static string ResolveStLinkSerial(ManagementObject portObj, string portInstanceId)
        {
            ManagementObject parent = FindUsbRootParent(portObj, portInstanceId);
            if (parent == null) return "";

            try
            {
                string parentId = parent.GetPropertyValue("PNPDeviceID")?.ToString() ?? "";
                Match m = Regex.Match(parentId, @"^USB\\VID_0483&PID_[0-9A-F]{4}\\([^\\]+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                    return m.Groups[1].Value;
            }
            catch { }

            return "";
        }
    }
}
