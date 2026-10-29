using System.Text;
using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class ParameterStatusMessageTests
    {
        [Fact]
        public void ShouldReadNameAndValueGivenUtf8StatusWhenPayloadIsParsed()
        {
            // Arrange
            byte[] payload = Encoding.UTF8.GetBytes("application_name\0café\0");

            // Act
            (string name, string value) = ParameterStatusMessage.Parse(payload);

            // Assert
            Assert.Equal("application_name", name);
            Assert.Equal("café", value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("DateStyle")]
        [InlineData("DateStyle\0")]
        [InlineData("DateStyle\0ISO")]
        [InlineData("DateStyle\0ISO\0extra")]
        public void ShouldRejectMalformedStatusGivenMissingTerminatorOrTrailingBytesWhenPayloadIsParsed(string text)
        {
            // Arrange
            byte[] payload = Encoding.UTF8.GetBytes(text);

            // Act
            void Parse()
            {
                _ = ParameterStatusMessage.Parse(payload);
            }

            // Assert
            _ = Assert.Throws<IOException>(Parse);
        }
    }
}
