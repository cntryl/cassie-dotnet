using System.Globalization;

namespace Cntryl.Cassie.Connection
{
    internal readonly record struct CassieDateStyle(CassieDateOutput Output, CassieDateOrder Order)
    {
        internal static CassieDateStyle Default { get; } = new(CassieDateOutput.Iso, CassieDateOrder.Mdy);

        internal static bool TryParse(string value, out CassieDateStyle style)
        {
            CassieDateOutput? output = null;
            CassieDateOrder? order = null;
            foreach (string component in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (component.ToUpperInvariant())
                {
                    case "ISO": output = CassieDateOutput.Iso; break;
                    case "SQL": output = CassieDateOutput.Sql; break;
                    case "POSTGRES": output = CassieDateOutput.Postgres; break;
                    case "GERMAN": output = CassieDateOutput.German; order = CassieDateOrder.Dmy; break;
                    case "MDY": order = CassieDateOrder.Mdy; break;
                    case "DMY": order = CassieDateOrder.Dmy; break;
                    case "YMD": order = CassieDateOrder.Ymd; break;
                    default:
                        style = default;
                        return false;
                }
            }

            if (output is null && order is null)
            {
                style = default;
                return false;
            }

            style = new CassieDateStyle(output ?? Default.Output, order ?? Default.Order);
            return true;
        }

        internal bool TryParseDate(string text, out DateOnly date)
        {
            string format = Output switch
            {
                CassieDateOutput.Iso => "yyyy-MM-dd",
                CassieDateOutput.Sql when Order == CassieDateOrder.Dmy => "dd/MM/yyyy",
                CassieDateOutput.Sql => "MM/dd/yyyy",
                CassieDateOutput.Postgres when Order == CassieDateOrder.Dmy => "dd-MM-yyyy",
                CassieDateOutput.Postgres => "MM-dd-yyyy",
                CassieDateOutput.German => "dd.MM.yyyy",
                _ => throw new InvalidOperationException("The Cassie date output style is invalid.")
            };

            return DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        internal string[] TimestampFormats()
        {
            string date = Order == CassieDateOrder.Dmy ? "ddd dd MMM" : "ddd MMM dd";
            return Output == CassieDateOutput.Postgres
                ? [date + " HH:mm:ss yyyy", date + " HH:mm:ss.FFFFFF yyyy"]
                : [];
        }
    }
}
