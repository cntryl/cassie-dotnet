using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class SessionMessagesTests
    {
        [Fact]
        public void ShouldWriteSslRequestCodeGivenTlsNegotiationWhenRequestIsBuilt()
        {
            // Arrange
            byte[] expected = Convert.FromHexString("0000000804D2162F");

            // Act
            byte[] packet = SessionMessages.SslRequest();

            // Assert
            Assert.Equal(expected, packet);
        }

        [Fact]
        public void ShouldPreserveBackendKeyBitsGivenCancellationWhenRequestIsBuilt()
        {
            // Arrange
            byte[] expected = Convert.FromHexString("0000001004D2162E0102030480000000");

            // Act
            byte[] packet = SessionMessages.CancelRequest(0x01020304, int.MinValue);

            // Assert
            Assert.Equal(expected, packet);
        }

        [Theory]
        [InlineData("", "0000004300030000757365720075006461746162617365006400636C69656E745F656E636F64696E670055544638006170706C69636174696F6E5F6E616D6500610000")]
        [InlineData("-c DateStyle=ISO", "0000005C00030000757365720075006461746162617365006400636C69656E745F656E636F64696E670055544638006170706C69636174696F6E5F6E616D650061006F7074696F6E73002D6320446174655374796C653D49534F0000")]
        public void ShouldWriteVersionThreeStartupGivenOptionalSettingsWhenPacketIsBuilt(string options, string expectedHex)
        {
            // Arrange
            byte[] expected = Convert.FromHexString(expectedHex);

            // Act
            byte[] packet = SessionMessages.Startup("u", "d", "a", options);

            // Assert
            Assert.Equal(expected, packet);
        }
    }
}
