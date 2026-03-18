namespace JoesScanner.Helpers
{
    public static class DeviceInfoHelper
    {
        public static string CombineManufacturerAndModel(string? manufacturer, string? model)
        {
            var mfg = (manufacturer ?? string.Empty).Trim();
            var mdl = (model ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(mfg))
                return mdl;

            if (string.IsNullOrWhiteSpace(mdl))
                return mfg;

            if (mdl.StartsWith(mfg, StringComparison.OrdinalIgnoreCase))
                return mdl;

            return mfg + " " + mdl;
        }
    }
}
