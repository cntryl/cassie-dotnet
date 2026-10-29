using System.Globalization;

namespace Cntryl.Cassie.Types
{
    /// <summary>Preserves a PostgreSQL numeric value without limiting its precision or scale.</summary>
    public readonly record struct CassieNumeric : IFormattable, IConvertible
    {
        /// <summary>Creates a numeric value from PostgreSQL's invariant text representation.</summary>
        /// <param name="value">A decimal number or PostgreSQL numeric special value.</param>
        /// <exception cref="FormatException">The input is not a supported numeric representation.</exception>
        public CassieNumeric(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsValid(value))
            {
                throw new FormatException("The value is not a valid PostgreSQL numeric representation.");
            }

            Value = value;
        }

        /// <summary>Gets the exact invariant numeric representation.</summary>
        public string Value { get; }

        /// <summary>Converts this value to decimal when it fits the CLR decimal range.</summary>
        public decimal ToDecimal()
        {
            return decimal.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Value ?? string.Empty;
        }

        /// <inheritdoc />
        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            return Value ?? string.Empty;
        }

        /// <summary>Converts to <see cref="decimal"/> when the value fits.</summary>
        public static explicit operator decimal(CassieNumeric value)
        {
            return value.ToDecimal();
        }

        TypeCode IConvertible.GetTypeCode()
        {
            return TypeCode.Object;
        }
        bool IConvertible.ToBoolean(IFormatProvider? provider)
        {
            return ToDecimal() != 0;
        }
        byte IConvertible.ToByte(IFormatProvider? provider)
        {
            return Convert.ToByte(ToDecimal(), provider);
        }
        char IConvertible.ToChar(IFormatProvider? provider)
        {
            throw new InvalidCastException("NUMERIC cannot convert to Char.");
        }
        DateTime IConvertible.ToDateTime(IFormatProvider? provider)
        {
            throw new InvalidCastException("NUMERIC cannot convert to DateTime.");
        }
        decimal IConvertible.ToDecimal(IFormatProvider? provider)
        {
            return ToDecimal();
        }
        double IConvertible.ToDouble(IFormatProvider? provider)
        {
            return double.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        short IConvertible.ToInt16(IFormatProvider? provider)
        {
            return Convert.ToInt16(ToDecimal(), provider);
        }
        int IConvertible.ToInt32(IFormatProvider? provider)
        {
            return Convert.ToInt32(ToDecimal(), provider);
        }
        long IConvertible.ToInt64(IFormatProvider? provider)
        {
            return Convert.ToInt64(ToDecimal(), provider);
        }
        sbyte IConvertible.ToSByte(IFormatProvider? provider)
        {
            return Convert.ToSByte(ToDecimal(), provider);
        }
        float IConvertible.ToSingle(IFormatProvider? provider)
        {
            return float.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        string IConvertible.ToString(IFormatProvider? provider)
        {
            return Value ?? string.Empty;
        }
        object IConvertible.ToType(Type conversionType, IFormatProvider? provider)
        {
            ArgumentNullException.ThrowIfNull(conversionType);
            return conversionType == typeof(CassieNumeric)
                ? this
                : conversionType == typeof(string)
                    ? Value ?? string.Empty
                    : Convert.ChangeType(ToDecimal(), conversionType, provider);
        }

        ushort IConvertible.ToUInt16(IFormatProvider? provider)
        {
            return Convert.ToUInt16(ToDecimal(), provider);
        }
        uint IConvertible.ToUInt32(IFormatProvider? provider)
        {
            return Convert.ToUInt32(ToDecimal(), provider);
        }
        ulong IConvertible.ToUInt64(IFormatProvider? provider)
        {
            return Convert.ToUInt64(ToDecimal(), provider);
        }

        private static bool IsValid(ReadOnlySpan<char> value)
        {
            if (value.SequenceEqual("NaN") || value.SequenceEqual("Infinity") || value.SequenceEqual("-Infinity") || value.SequenceEqual("+Infinity"))
            {
                return true;
            }

            int index = 0;
            if (index < value.Length && value[index] is '+' or '-')
            {
                index++;
            }

            int digits = ConsumeDigits(value, ref index);
            if (index < value.Length && value[index] == '.')
            {
                index++;
                digits += ConsumeDigits(value, ref index);
            }

            if (digits == 0)
            {
                return false;
            }

            if (index < value.Length && value[index] is 'e' or 'E')
            {
                index++;
                if (index < value.Length && value[index] is '+' or '-')
                {
                    index++;
                }

                if (ConsumeDigits(value, ref index) == 0)
                {
                    return false;
                }
            }

            return index == value.Length;
        }

        private static int ConsumeDigits(ReadOnlySpan<char> value, ref int index)
        {
            int start = index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                index++;
            }

            return index - start;
        }
    }
}
