namespace IAMS.Web.Services;

/// <summary>
/// Service for looking up Apple device information from serial numbers.
/// Supports 12-character serial numbers (2010 and later).
/// </summary>
public class AppleDeviceLookupService
{
    /// <summary>
    /// Attempts to identify an Apple device from its serial number.
    /// </summary>
    /// <param name="serialNumber">The device serial number</param>
    /// <returns>Device info if recognized, null otherwise</returns>
    public AppleDeviceInfo? LookupDevice(string? serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
            return null;

        var serial = serialNumber.Trim().ToUpperInvariant();

        // Apple serial numbers are typically 11-12 characters
        if (serial.Length < 11 || serial.Length > 12)
            return null;

        // Extract model identifier (last 4 characters for 12-char serials)
        var modelCode = serial.Length == 12 ? serial[8..] : serial[7..];

        // Look up in our mapping table
        if (ModelMapping.TryGetValue(modelCode, out var deviceInfo))
        {
            // Try to determine year from serial if not in mapping
            var year = deviceInfo.Year ?? ParseYearFromSerial(serial);

            return new AppleDeviceInfo
            {
                Manufacturer = "Apple",
                Model = deviceInfo.Model,
                Year = year,
                DeviceType = deviceInfo.DeviceType,
                ModelCode = modelCode,
                IsAppleDevice = true
            };
        }

        // Check if it looks like an Apple serial (alphanumeric, proper length)
        if (IsLikelyAppleSerial(serial))
        {
            var year = ParseYearFromSerial(serial);
            var guessedType = GuessDeviceTypeFromCode(modelCode);

            return new AppleDeviceInfo
            {
                Manufacturer = "Apple",
                Model = null, // Unknown model
                Year = year,
                DeviceType = guessedType,
                ModelCode = modelCode,
                IsAppleDevice = true,
                IsPartialMatch = true
            };
        }

        return null;
    }

    /// <summary>
    /// Checks if a serial number appears to be from an Apple device
    /// </summary>
    public bool IsAppleSerial(string? serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
            return false;

        var serial = serialNumber.Trim().ToUpperInvariant();
        return IsLikelyAppleSerial(serial);
    }

    private static bool IsLikelyAppleSerial(string serial)
    {
        // Apple serials are alphanumeric, 11-12 chars, no special characters
        if (serial.Length < 11 || serial.Length > 12)
            return false;

        foreach (var c in serial)
        {
            if (!char.IsLetterOrDigit(c))
                return false;
        }

        // Common Apple manufacturing location prefixes
        var prefix = serial[..2];
        var knownPrefixes = new[] { "C0", "C1", "C2", "C3", "C7", "D2", "DM", "DN", "DP", "DQ", "DT", "DV", "DX", "DY", "F1", "F2", "F5", "F7", "FC", "FK", "FM", "FQ", "FV", "G8", "GC", "GD", "GQ", "GT", "GV", "GW", "GX", "GY", "H0", "H1", "H2", "H5", "J2", "JC", "JD", "MC", "ML", "MM", "MN", "MQ", "MT", "MV", "MW", "MX", "NX", "QP", "RH", "RJ", "RK", "RM", "RN", "RP", "SC", "SG", "SQ", "VM", "VN", "VQ", "VR", "W8", "WQ", "XC", "XQ", "YM" };

        return knownPrefixes.Any(p => serial.StartsWith(p));
    }

    private static int? ParseYearFromSerial(string serial)
    {
        if (serial.Length < 4)
            return null;

        // Character 4 indicates year for 12-char serials
        var yearChar = serial[3];

        // Year encoding: C=2010/2020, D=2010/2020, F=2011/2021, etc.
        return yearChar switch
        {
            'C' or 'D' => 2020, // Could also be 2010
            'F' or 'G' => 2021, // Could also be 2011
            'H' or 'J' => 2022, // Could also be 2012
            'K' or 'L' => 2023, // Could also be 2013
            'M' or 'N' => 2024, // Could also be 2014
            'P' or 'Q' => 2025, // Could also be 2015
            'R' or 'T' => 2026, // Could also be 2016
            'V' or 'W' => 2027, // Could also be 2017
            'X' or 'Y' => 2028, // Could also be 2018
            '1' or '2' => 2021,
            '3' or '4' => 2023,
            '5' or '6' => 2025,
            '7' or '8' => 2027,
            '9' or '0' => 2019, // Older format
            _ => null
        };
    }

    private static string? GuessDeviceTypeFromCode(string modelCode)
    {
        if (modelCode.Length < 2)
            return null;

        // Common Apple model code patterns
        var firstTwo = modelCode[..2];

        return firstTwo switch
        {
            "LL" or "LZ" or "DN" or "QL" => "Laptop", // MacBooks
            "GR" or "G8" or "0F" or "Z0" => "Desktop", // iMac, Mac Pro
            "NE" or "QN" or "FK" or "NF" or "NR" => "Phone", // iPhones
            "LK" or "LM" or "LN" or "LX" => "Tablet", // iPads
            "LW" or "ML" or "HK" or "J0" => "Tablet", // iPad variants
            "HN" or "QJ" or "MX" => "Phone", // iPhone variants
            "0Q" or "M9" => "Desktop", // Mac Mini
            _ => null
        };
    }

    /// <summary>
    /// Local mapping of Apple model codes to device information.
    /// This is a curated list of common Apple devices for offline lookup.
    /// </summary>
    private static readonly Dictionary<string, (string Model, string DeviceType, int? Year)> ModelMapping = new()
    {
        // MacBook Pro 14" (2021-2024)
        { "QP2H", ("MacBook Pro 14\" M1 Pro", "Laptop", 2021) },
        { "QP2J", ("MacBook Pro 14\" M1 Max", "Laptop", 2021) },
        { "R4N2", ("MacBook Pro 14\" M2 Pro", "Laptop", 2023) },
        { "R4N3", ("MacBook Pro 14\" M2 Max", "Laptop", 2023) },
        { "R4N6", ("MacBook Pro 14\" M2 Pro", "Laptop", 2023) },
        { "XCM2", ("MacBook Pro 14\" M3", "Laptop", 2023) },
        { "XCM3", ("MacBook Pro 14\" M3 Pro", "Laptop", 2023) },
        { "XCM4", ("MacBook Pro 14\" M3 Max", "Laptop", 2023) },
        { "RY0C", ("MacBook Pro 14\" M4", "Laptop", 2024) },
        { "RY0D", ("MacBook Pro 14\" M4 Pro", "Laptop", 2024) },
        { "RY0E", ("MacBook Pro 14\" M4 Max", "Laptop", 2024) },

        // MacBook Pro 16" (2021-2024)
        { "QP2K", ("MacBook Pro 16\" M1 Pro", "Laptop", 2021) },
        { "QP2L", ("MacBook Pro 16\" M1 Max", "Laptop", 2021) },
        { "R4N4", ("MacBook Pro 16\" M2 Pro", "Laptop", 2023) },
        { "R4N5", ("MacBook Pro 16\" M2 Max", "Laptop", 2023) },
        { "XCM5", ("MacBook Pro 16\" M3 Pro", "Laptop", 2023) },
        { "XCM6", ("MacBook Pro 16\" M3 Max", "Laptop", 2023) },
        { "RY0F", ("MacBook Pro 16\" M4 Pro", "Laptop", 2024) },
        { "RY0G", ("MacBook Pro 16\" M4 Max", "Laptop", 2024) },

        // MacBook Pro 13" (2020-2022)
        { "QL2H", ("MacBook Pro 13\" M1", "Laptop", 2020) },
        { "QL2G", ("MacBook Pro 13\" M1", "Laptop", 2020) },
        { "R4N1", ("MacBook Pro 13\" M2", "Laptop", 2022) },

        // MacBook Air (2020-2024)
        { "QL3D", ("MacBook Air 13\" M1", "Laptop", 2020) },
        { "QL3C", ("MacBook Air 13\" M1", "Laptop", 2020) },
        { "R4MX", ("MacBook Air 13\" M2", "Laptop", 2022) },
        { "R4MY", ("MacBook Air 13\" M2 Midnight", "Laptop", 2022) },
        { "R4N0", ("MacBook Air 15\" M2", "Laptop", 2023) },
        { "XMT2", ("MacBook Air 13\" M3", "Laptop", 2024) },
        { "XMT3", ("MacBook Air 15\" M3", "Laptop", 2024) },

        // MacBook 12" (2015-2017)
        { "GH5K", ("MacBook 12\"", "Laptop", 2015) },
        { "H3QY", ("MacBook 12\"", "Laptop", 2016) },
        { "J1MK", ("MacBook 12\"", "Laptop", 2017) },

        // iMac 24" (2021-2024)
        { "QP0W", ("iMac 24\" M1", "Desktop", 2021) },
        { "QP0X", ("iMac 24\" M1", "Desktop", 2021) },
        { "R5K2", ("iMac 24\" M3", "Desktop", 2023) },
        { "R5K3", ("iMac 24\" M3", "Desktop", 2023) },
        { "RY04", ("iMac 24\" M4", "Desktop", 2024) },

        // Mac Mini (2020-2024)
        { "QL5M", ("Mac mini M1", "Desktop", 2020) },
        { "R5MK", ("Mac mini M2", "Desktop", 2023) },
        { "R5ML", ("Mac mini M2 Pro", "Desktop", 2023) },
        { "RY01", ("Mac mini M4", "Desktop", 2024) },
        { "RY02", ("Mac mini M4 Pro", "Desktop", 2024) },

        // Mac Studio (2022-2024)
        { "R4H2", ("Mac Studio M1 Max", "Desktop", 2022) },
        { "R4H3", ("Mac Studio M1 Ultra", "Desktop", 2022) },
        { "R5M7", ("Mac Studio M2 Max", "Desktop", 2023) },
        { "R5M8", ("Mac Studio M2 Ultra", "Desktop", 2023) },

        // Mac Pro (2019-2023)
        { "PN0P", ("Mac Pro 2019", "Desktop", 2019) },
        { "R5TN", ("Mac Pro M2 Ultra", "Desktop", 2023) },

        // iPhone 15 Series
        { "0Q1W", ("iPhone 15", "Phone", 2023) },
        { "0Q1X", ("iPhone 15 Plus", "Phone", 2023) },
        { "0Q1Y", ("iPhone 15 Pro", "Phone", 2023) },
        { "0Q1Z", ("iPhone 15 Pro Max", "Phone", 2023) },

        // iPhone 16 Series
        { "0R0W", ("iPhone 16", "Phone", 2024) },
        { "0R0X", ("iPhone 16 Plus", "Phone", 2024) },
        { "0R0Y", ("iPhone 16 Pro", "Phone", 2024) },
        { "0R0Z", ("iPhone 16 Pro Max", "Phone", 2024) },

        // iPhone 14 Series
        { "0P9H", ("iPhone 14", "Phone", 2022) },
        { "0P9J", ("iPhone 14 Plus", "Phone", 2022) },
        { "0P9K", ("iPhone 14 Pro", "Phone", 2022) },
        { "0P9L", ("iPhone 14 Pro Max", "Phone", 2022) },

        // iPhone 13 Series
        { "HN7G", ("iPhone 13 mini", "Phone", 2021) },
        { "HN7H", ("iPhone 13", "Phone", 2021) },
        { "HN7J", ("iPhone 13 Pro", "Phone", 2021) },
        { "HN7K", ("iPhone 13 Pro Max", "Phone", 2021) },

        // iPhone 12 Series
        { "QNYE", ("iPhone 12 mini", "Phone", 2020) },
        { "QNYF", ("iPhone 12", "Phone", 2020) },
        { "QNYG", ("iPhone 12 Pro", "Phone", 2020) },
        { "QNYH", ("iPhone 12 Pro Max", "Phone", 2020) },

        // iPhone SE
        { "P7P7", ("iPhone SE (2nd gen)", "Phone", 2020) },
        { "QJR9", ("iPhone SE (3rd gen)", "Phone", 2022) },

        // iPad Pro 11" (2018-2024)
        { "LW4J", ("iPad Pro 11\" (1st gen)", "Tablet", 2018) },
        { "LW4K", ("iPad Pro 11\" (1st gen)", "Tablet", 2018) },
        { "MXF6", ("iPad Pro 11\" (2nd gen)", "Tablet", 2020) },
        { "N5WN", ("iPad Pro 11\" M1", "Tablet", 2021) },
        { "N5WP", ("iPad Pro 11\" M1", "Tablet", 2021) },
        { "R4KN", ("iPad Pro 11\" M2", "Tablet", 2022) },
        { "XMT0", ("iPad Pro 11\" M4", "Tablet", 2024) },

        // iPad Pro 12.9" (2018-2024)
        { "LW4L", ("iPad Pro 12.9\" (3rd gen)", "Tablet", 2018) },
        { "LW4M", ("iPad Pro 12.9\" (3rd gen)", "Tablet", 2018) },
        { "MXF7", ("iPad Pro 12.9\" (4th gen)", "Tablet", 2020) },
        { "N5WQ", ("iPad Pro 12.9\" M1", "Tablet", 2021) },
        { "N5WR", ("iPad Pro 12.9\" M1", "Tablet", 2021) },
        { "R4KP", ("iPad Pro 12.9\" M2", "Tablet", 2022) },
        { "XMT1", ("iPad Pro 13\" M4", "Tablet", 2024) },

        // iPad Air (2020-2024)
        { "MYF4", ("iPad Air (4th gen)", "Tablet", 2020) },
        { "MYF5", ("iPad Air (4th gen)", "Tablet", 2020) },
        { "QKD4", ("iPad Air M1", "Tablet", 2022) },
        { "R4KM", ("iPad Air 11\" M2", "Tablet", 2024) },
        { "R4KL", ("iPad Air 13\" M2", "Tablet", 2024) },

        // iPad (2019-2024)
        { "MWF2", ("iPad (7th gen)", "Tablet", 2019) },
        { "MYLD", ("iPad (8th gen)", "Tablet", 2020) },
        { "N39F", ("iPad (9th gen)", "Tablet", 2021) },
        { "QK5T", ("iPad (10th gen)", "Tablet", 2022) },

        // iPad mini (2019-2024)
        { "MV0K", ("iPad mini (5th gen)", "Tablet", 2019) },
        { "QKD2", ("iPad mini (6th gen)", "Tablet", 2021) },
        { "QKD3", ("iPad mini (6th gen)", "Tablet", 2021) },

        // Apple Watch Ultra
        { "QKDJ", ("Apple Watch Ultra", "Peripheral", 2022) },
        { "R5RL", ("Apple Watch Ultra 2", "Peripheral", 2023) },

        // Apple Watch Series 9/10
        { "R5RK", ("Apple Watch Series 9", "Peripheral", 2023) },
        { "RY11", ("Apple Watch Series 10", "Peripheral", 2024) },

        // AirPods
        { "QKDW", ("AirPods Pro (2nd gen)", "Peripheral", 2022) },
        { "RY1A", ("AirPods 4", "Peripheral", 2024) },
        { "RY1B", ("AirPods 4 ANC", "Peripheral", 2024) },
        { "RY1C", ("AirPods Max", "Peripheral", 2024) },

        // Apple TV
        { "QK5G", ("Apple TV 4K (3rd gen)", "Peripheral", 2022) },
        { "QK5H", ("Apple TV 4K (3rd gen) 128GB", "Peripheral", 2022) },

        // HomePod
        { "R4K7", ("HomePod (2nd gen)", "Peripheral", 2023) },
        { "P8PQ", ("HomePod mini", "Peripheral", 2020) },

        // Studio Display / Pro Display
        { "R4H1", ("Studio Display", "Monitor", 2022) },
        { "PNPR", ("Pro Display XDR", "Monitor", 2019) },
    };
}

/// <summary>
/// Information about an Apple device derived from its serial number
/// </summary>
public class AppleDeviceInfo
{
    public required string Manufacturer { get; init; }
    public string? Model { get; init; }
    public int? Year { get; init; }
    public string? DeviceType { get; init; }
    public string? ModelCode { get; init; }
    public bool IsAppleDevice { get; init; }
    public bool IsPartialMatch { get; init; }
}
