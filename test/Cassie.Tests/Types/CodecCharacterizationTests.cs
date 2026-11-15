using Cntryl.Cassie.Types;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using Xunit;

namespace Cntryl.Cassie.Tests.Types
{
    /// <summary>Pins the observable output of the result and parameter codecs for every built-in type, whatever implements them.</summary>
    public sealed class CodecCharacterizationTests
    {
        [Fact]
        public void ShouldProduceRecordedOutcomesGivenBuiltInTypesWhenCodecsAreInvoked()
        {
            // Arrange
            List<string> actual = [];

            // Act
            foreach ((string label, int oid, object? value) in EncodeCases())
            {
                actual.Add($"binary {label}: {Run(() => Describe(CassieParameterCodec.GetBinaryParameter(value, oid)))}");
                if (value is not null and not DBNull)
                {
                    actual.Add($"text {label}: {Run(() => Describe(CassieParameterCodec.EncodeTextParameter(value, oid)))}");
                }
            }

            foreach ((int oid, string text) in TextDecodeCases())
            {
                actual.Add($"decode-text {oid} '{text}': {Run(() => Describe(CassieResultCodec.ConvertValue(text, oid)))}");
            }

            foreach ((int oid, string hex) in BinaryDecodeCases())
            {
                actual.Add($"decode-binary {oid} {hex}: {Run(() => Describe(CassieResultCodec.ConvertBinaryValue(Convert.FromHexString(hex), oid)))}");
            }

            // Assert
            Assert.Equal(CodecGolden.Lines, actual);
        }

        private static string Run(Func<string> action)
        {
            try
            {
                return action();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return "ex:" + exception.GetType().Name;
            }
        }

        private static string Describe(object? value)
        {
            return value switch
            {
                null => "null",
                byte[] bytes => Convert.ToHexString(bytes),
                CassieArray array => $"array<{array.ElementTypeOid}>[{string.Join(",", array.Dimensions)}]{{{string.Join(";", array.Elements.Select(Describe))}}}",
                CassieVector vector => "vector(" + string.Join(",", vector.Values.Select(static v => v.ToString("R", CultureInfo.InvariantCulture))) + ")",
                DateTime dateTime => $"DateTime:{dateTime.Ticks}/{dateTime.Kind}",
                DateTimeOffset offset => $"DateTimeOffset:{offset.UtcTicks}/{offset.Offset}",
                CassieTemporalValue temporal => $"Temporal:{temporal.Kind}/{temporal.PostgresTypeOid}/{temporal}",
                CassieTimeWithTimeZone timeWithZone => $"CassieTimeWithTimeZone:{timeWithZone.Time.Ticks}/{timeWithZone.OffsetSeconds.ToString(CultureInfo.InvariantCulture)}",
                IFormattable formattable => value.GetType().Name + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.GetType().Name + ":" + value
            };
        }

        private static IEnumerable<(string Label, int Oid, object? Value)> EncodeCases()
        {
            string longName = new('n', 64);
            CassieNetworkAddress ipv4 = new(IPAddress.Parse("192.168.1.5"), 32, isCidr: false);
            CassieNetworkAddress cidr4 = new(IPAddress.Parse("10.0.0.0"), 8, isCidr: true);
            CassieNetworkAddress ipv6 = new(IPAddress.Parse("2001:db8::1"), 128, isCidr: false);
            PhysicalAddress mac6 = PhysicalAddress.Parse("08-00-2B-01-02-03");
            PhysicalAddress mac8 = PhysicalAddress.Parse("08-00-2B-01-02-03-04-05");
            yield return ("bool-true", 16, true);
            yield return ("bool-false", 16, false);
            yield return ("bool-string", 16, "x");
            yield return ("bytea", 17, new byte[] { 1, 2, 255 });
            yield return ("char-string", 18, "a");
            yield return ("char-char", 18, 'c');
            yield return ("name-string", 19, "name");
            yield return ("name-char", 19, 'c');
            yield return ("name-long", 19, longName);
            yield return ("text-string", 25, "héllo");
            yield return ("text-char", 25, 'x');
            yield return ("text-int", 25, 5);
            yield return ("bpchar", 1042, "ab ");
            yield return ("varchar", 1043, "vc");
            yield return ("json-string", 114, "{}");
            yield return ("json-char", 114, 'c');
            yield return ("jsonb", 3802, /*lang=json,strict*/ "{\"a\":1}");
            yield return ("jsonb-int", 3802, 1);
            yield return ("jsonpath", 4072, "$.a");
            yield return ("void", 2278, "x");
            yield return ("unknown", 705, "x");
            yield return ("int8", 20, long.MinValue);
            yield return ("int8-int", 20, 5);
            yield return ("int2-short", 21, (short)-2);
            yield return ("int2-byte", 21, (byte)200);
            yield return ("int2-sbyte", 21, (sbyte)-3);
            yield return ("int2-int", 21, 5);
            yield return ("int4-int", 23, int.MinValue);
            yield return ("int4-ushort", 23, ushort.MaxValue);
            yield return ("int4-short", 23, (short)1);
            yield return ("oid", 26, uint.MaxValue);
            yield return ("regclass", 2205, 5u);
            yield return ("oid-int", 26, 5);
            yield return ("float4", 700, 1.5f);
            yield return ("float4-nan", 700, float.NaN);
            yield return ("float4-double", 700, 1.5d);
            yield return ("float8", 701, -2.25d);
            yield return ("float8-float", 701, 1.5f);
            yield return ("numeric-cassie", 1700, new CassieNumeric("12.50"));
            yield return ("numeric-decimal", 1700, 12.5m);
            yield return ("numeric-negative", 1700, -0.0001m);
            yield return ("numeric-large", 1700, 123456789012345678.123456789m);
            yield return ("numeric-ulong", 1700, ulong.MaxValue);
            yield return ("numeric-nan", 1700, new CassieNumeric("NaN"));
            yield return ("numeric-double", 1700, 1.5d);
            yield return ("date-dateonly", 1082, new DateOnly(2024, 2, 29));
            yield return ("date-datetime", 1082, new DateTime(2024, 2, 29, 13, 0, 0, DateTimeKind.Utc));
            yield return ("time-timeonly", 1083, new TimeOnly(13, 14, 15, 123));
            yield return ("time-timespan", 1083, new TimeSpan(0, 13, 14, 15, 123));
            yield return ("time-timespan-24", 1083, TimeSpan.FromHours(24));
            yield return ("timestamp", 1114, new DateTime(2024, 2, 29, 13, 14, 15, DateTimeKind.Unspecified).AddTicks(1230));
            yield return ("timestamp-utc", 1114, new DateTime(2024, 2, 29, 13, 14, 15, DateTimeKind.Utc));
            yield return ("timestamptz", 1184, new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.FromHours(2)));
            yield return ("timestamptz-datetime", 1184, new DateTime(2024, 2, 29, 13, 14, 15, DateTimeKind.Utc));
            yield return ("interval", 1186, new CassieInterval(14, -3, 4_500_000));
            yield return ("timetz", 1266, new CassieTimeWithTimeZone(new TimeOnly(1, 2, 3), -7200));
            yield return ("temporal-date", 1082, CassieTemporalValue.FromDateDays(int.MaxValue));
            yield return ("temporal-date-wrong-oid", 1114, CassieTemporalValue.FromDateDays(10));
            yield return ("temporal-timestamp", 1114, CassieTemporalValue.FromTimestampMicroseconds(long.MinValue));
            yield return ("temporal-timestamptz", 1184, CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds(long.MaxValue));
            yield return ("temporal-time", 1083, CassieTemporalValue.FromTimeMicroseconds(86_400_000_000));
            yield return ("temporal-timetz", 1266, CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(5, 3600));
            yield return ("inet-v4", 869, ipv4);
            yield return ("inet-v6", 869, ipv6);
            yield return ("inet-as-cidr", 650, ipv4);
            yield return ("cidr", 650, cidr4);
            yield return ("cidr-as-inet", 869, cidr4);
            yield return ("inet-string", 869, "1.2.3.4");
            yield return ("mac6", 829, mac6);
            yield return ("mac8", 774, mac8);
            yield return ("mac6-as-mac8", 774, mac6);
            yield return ("bit", 1560, new CassieBitString("10101", isVarying: false));
            yield return ("varbit", 1562, new CassieBitString("1010", isVarying: true));
            yield return ("bit-as-varbit", 1562, new CassieBitString("1010", isVarying: false));
            yield return ("bit-empty", 1560, new CassieBitString("", isVarying: false));
            yield return ("lsn", 3220, new CassieLsn(0x16B374D848UL));
            yield return ("lsn-text-oid", 25, new CassieLsn(5));
            yield return ("vector3", 100003, new CassieVector(1f, 2.5f, -3f));
            yield return ("vector-wrong-dim", 100002, new CassieVector(1f, 2.5f, -3f));
            yield return ("vector-nan", 100001, new CassieVector(1f));
            yield return ("uuid", 2950, new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
            yield return ("uuid-string", 2950, "6f9619ff-8b86-d011-b42d-00c04fc964ff");
            yield return ("int4-array", 1007, new[] { 1, 2, 3 });
            yield return ("text-array", 1009, new[] { "a", "b c", "d,e" });
            yield return ("float8-array-nan", 1022, new[] { 1.5, double.NaN });
            yield return ("name-array", 1003, new[] { "a", "b" });
            yield return ("cassie-array", 1007, new CassieArray(23, [2], [1], [1, 2]));
            yield return ("null-int4", 23, null);
            yield return ("dbnull-int4", 23, DBNull.Value);
            yield return ("null-text", 25, null);
            yield return ("null-numeric", 1700, DBNull.Value);
            yield return ("null-int4-array", 1007, null);
            yield return ("null-lsn", 3220, null);
        }

        private static IEnumerable<(int Oid, string Text)> TextDecodeCases()
        {
            (int, string)[] cases =
            [
                (16, "t"), (16, "FALSE"), (16, "yes"), (17, "\\x0aff"), (17, "\\x0a0"), (17, "0aff"),
                (18, "x"), (19, "name"), (20, "-9223372036854775808"), (20, "x"), (21, "-5"), (21, "40000"), (23, "42"), (23, " 7"),
                (26, "4294967295"), (26, "-1"), (2205, "5"), (700, "1.5"), (700, "NaN"), (701, "-0.25"), (701, "1e400"),
                (1700, "12.50"), (1700, "NaN"), (1700, "1e5"), (1700, "abc"), (2950, "6f9619ff-8b86-d011-b42d-00c04fc964ff"), (2950, "zz"),
                (1082, "2024-02-29"), (1082, "infinity"), (1082, "0001-01-01 BC"), (1083, "13:14:15.123456"), (1083, "24:00:00"),
                (1114, "2024-02-29 13:14:15.5"), (1114, "-infinity"), (1114, "x"), (1184, "2024-02-29 13:14:15+02"), (1184, "2024-02-29 13:14:15.25+05:30"), (1184, "infinity"),
                (1266, "13:14:15+02"), (1266, "01:02:03-07:30"), (1186, "1 day"), (114, "{}"), (3802, "{}"), (2278, ""), (705, "x"),
                (650, "10.0.0.0/8"), (869, "1.2.3.4"), (829, "08:00:2b:01:02:03"), (1560, "101"), (3220, "16/B374D848"), (100003, "[1,2,3]"), (1007, "{1,2}"), (9999, "z")
            ];
            return cases.Select(static c => (c.Item1, c.Item2));
        }

        private static IEnumerable<(int Oid, string Hex)> BinaryDecodeCases()
        {
            (int, string)[] cases =
            [
                (16, "01"), (16, "00"), (16, "02"), (16, ""), (16, "0001"), (17, "0aff"), (17, ""),
                (18, "61"), (19, "6162"), (25, "68C3A9"), (1042, "6120"), (1043, "76"), (114, "7B7D"),
                (20, "FFFFFFFFFFFFFFFE"), (20, "0102"), (21, "FFFE"), (21, "01"), (23, "00010000"), (23, ""),
                (24, "00000005"), (26, "FFFFFFFF"), (26, "0000"), (2205, "00000007"), (4191, "01020304"), (4191, "01"),
                (700, "3FC00000"), (700, "3FC000"), (701, "BFD0000000000000"), (701, "7FF8000000000000"), (701, "00"),
                (705, "61"), (2249, "00"), (2278, ""), (2278, "00"),
                (1700, "00010000000000040001"), (1700, "0000000000000000"), (1700, "0000000000C00000"), (1700, "00"),
                (1082, "00000000"), (1082, "7FFFFFFF"), (1082, "FFFFFFFF"), (1082, "0000"),
                (1083, "0000000000000001"), (1083, "00000014FB000000"), (1083, "0000000000000000"), (1083, "FFFFFFFFFFFFFFFF"),
                (1114, "0000000000000000"), (1114, "7FFFFFFFFFFFFFFF"), (1114, "FFFFFFFFFFFFFFFF"), (1114, "00"),
                (1184, "0000000100000000"), (1184, "8000000000000000"), (1184, "0000"),
                (1186, "00000000000F42400000000300000001"), (1186, "00"), (1266, "0000000000000001FFFFE3E0"), (1266, "00"),
                (650, "020808010A000000"), (869, "0220000401020304"), (869, "02"), (774, "08002B0102030405"), (829, "08002B010203"), (829, "08"),
                (1560, "0000000580"), (1562, "0000000480"), (1560, "00"), (3220, "00000016B374D848"), (3220, "00"),
                (100003, "00030000" + "3F800000" + "40200000" + "C0400000"), (100003, "0003000000"), (100002, "0003000000"),
                (2950, "6F9619FF8B86D011B42D00C04FC964FF"), (2950, "6F"),
                (3802, "017B7D"), (3802, "007B7D"), (3802, ""), (4072, "01242E61"), (4072, "00"), (4072, ""),
                (1007, "00000001000000000000001700000002000000010000000400000001000000040000000200000004"),
                (1007, "000000010000000000000017000000010000000100000004" + "00000001"), (1007, "000000000000000000000017"),
                (1231, "000000010000000000000017000000010000000100000000" + "00000008" + "0001000000000000"),
                (9999, "00")
            ];
            return cases.Select(static c => (c.Item1, c.Item2));
        }
    }
}
