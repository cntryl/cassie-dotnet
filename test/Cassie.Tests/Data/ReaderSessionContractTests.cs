using Cntryl.Cassie.Data;
using System.Data;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Data
{
    public sealed class ReaderSessionContractTests
    {
        [Theory]
        [InlineData("initialize", false)]
        [InlineData("read", false)]
        [InlineData("next", false)]
        [InlineData("single-row", false)]
        [InlineData("single-result", false)]
        [InlineData("initialize", true)]
        [InlineData("read", true)]
        [InlineData("next", true)]
        [InlineData("single-row", true)]
        [InlineData("single-result", true)]
        public async Task ShouldDrainAndTranslateServerErrorGivenErrorResponseWhenReaderPhaseFails(string phase, bool canceled)
        {
            // Arrange
            FakeReaderSession session = new();
            if (phase != "initialize")
            {
                session.Enqueue((byte)'T', OneInt32Column(binary: false));
            }

            if (phase == "next")
            {
                session.Enqueue((byte)'C', Encoding.UTF8.GetBytes("SELECT 0\0"));
            }
            else if (phase == "single-row")
            {
                session.Enqueue((byte)'D', Convert.FromHexString("00010000000131"));
            }

            string sqlState = canceled ? "57014" : "23505";
            session.Enqueue((byte)'E', Encoding.UTF8.GetBytes($"SERROR\0C{sqlState}\0Mquery failed\0Ddetails\0\0"));
            byte[] parameterStatus = Encoding.UTF8.GetBytes("DateStyle\0ISO, MDY\0");
            session.Enqueue((byte)'S', parameterStatus);
            session.Enqueue((byte)'Z', [(byte)'I']);
            CommandBehavior behavior = phase switch
            {
                "single-row" => CommandBehavior.SingleRow,
                "single-result" => CommandBehavior.SingleResult,
                _ => CommandBehavior.Default
            };
            await using CassieDataReader reader = new(session, commandTimeout: 0, behavior);
            using CancellationTokenSource caller = new();
            if (phase != "initialize")
            {
                await reader.InitializeAsync(TestContext.Current.CancellationToken);
            }

            if (phase is "next" or "single-row")
            {
                _ = await reader.ReadAsync(TestContext.Current.CancellationToken);
            }

            if (canceled)
            {
                await caller.CancelAsync();
            }

            // Act
            Exception? failure = await Record.ExceptionAsync(async () => await RunPhaseAsync(reader, phase, caller.Token));

            // Assert
            CassieException serverError;
            if (canceled)
            {
                OperationCanceledException error = Assert.IsType<OperationCanceledException>(failure);
                Assert.Equal(caller.Token, error.CancellationToken);
                serverError = Assert.IsType<CassieException>(error.InnerException);
            }
            else
            {
                serverError = Assert.IsType<CassieException>(failure);
            }

            Assert.Equal(sqlState, serverError.SqlState);
            Assert.Equal("query failed", serverError.Message);
            Assert.Equal("details", serverError.Detail);
            Assert.Equal(parameterStatus, Assert.Single(session.ParameterStatuses));
            Assert.Equal("I"u8.ToArray(), Assert.Single(session.ReadyMessages));
            Assert.Equal(1, session.ReaderClosures);
            Assert.Equal(0, session.Aborts);
            Assert.Equal(0, session.ConnectionClosures);
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldRejectStaleColumnFormatsGivenResultMetadataResetWhenMalformedRowArrives()
        {
            // Arrange
            FakeReaderSession session = new();
            session.Enqueue((byte)'T', OneInt32Column(binary: true));
            session.Enqueue((byte)'C', Encoding.UTF8.GetBytes("SELECT 0\0"));
            session.Enqueue((byte)'s', []);
            session.Enqueue((byte)'D', Convert.FromHexString("0001000000040000002A"));
            session.Enqueue((byte)'Z', [(byte)'I']);
            await using CassieDataReader reader = new(session, commandTimeout: 0, CommandBehavior.Default);
            await reader.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Task<bool> malformedResult = reader.NextResultAsync(TestContext.Current.CancellationToken);
            _ = await Assert.ThrowsAsync<IOException>(() => malformedResult);
            Assert.Equal(0, reader.FieldCount);

            // Act
            Task<bool> malformedRow = reader.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            IOException error = await Assert.ThrowsAsync<IOException>(() => malformedRow);
            Assert.Equal("DataRow column count did not match RowDescription.", error.Message);
        }

        private static byte[] OneInt32Column(bool binary)
        {
            byte[] payload = Convert.FromHexString("000176616C756500000000000000000000170004FFFFFFFF0000");
            payload[^1] = binary ? (byte)1 : (byte)0;
            return payload;
        }

        private static async Task RunPhaseAsync(CassieDataReader reader, string phase, CancellationToken cancellationToken)
        {
            switch (phase)
            {
                case "initialize":
                    await reader.InitializeAsync(cancellationToken);
                    break;
                case "next":
                case "single-result":
                    _ = await reader.NextResultAsync(cancellationToken);
                    break;
                default:
                    _ = await reader.ReadAsync(cancellationToken);
                    break;
            }
        }
    }
}
