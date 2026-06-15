using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Loam.Revit.Connector.Profiles
{
    /// Profile = firm-specific parameter and category mappings.
    /// Per the spec, door/room field names come from here (nl.json by default).
    public class Profile
    {
        public string Id { get; set; } = "nl";

        /// Parameter names on doors carrying clear width in mm (or metres).
        public List<string> DoorClearWidthParams { get; set; } = new();

        /// Parameter names on rooms carrying the function label
        /// (Bbl-4.180 keys on this — e.g. "verblijfsruimte", "hal").
        public List<string> RoomFunctionParams { get; set; } = new();

        /// Parameter names carrying the classification code (NL-SfB / Omniclass / Assembly).
        public List<string> ClassificationCodeParams { get; set; } = new();
        public List<string> ClassificationDescriptionParams { get; set; } = new();

        /// Regex the classification code must match (e.g. NL-SfB: ^\d{1,2}(\.\d{1,3})?$).
        public string ClassificationCodeRegex { get; set; }

        /// Width tokens are read from this param on the door type name (default: type name itself).
        public string DoorTypeNameParam { get; set; }
    }

    public static class ProfileLoader
    {
        public static Profile Load(string path)
        {
            if (!File.Exists(path)) return new Profile();
            return JsonConvert.DeserializeObject<Profile>(File.ReadAllText(path)) ?? new Profile();
        }
    }
}
