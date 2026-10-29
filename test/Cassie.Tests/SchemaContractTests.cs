using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class SchemaContractTests
    {
        [Fact]
        public void ShouldAdvertiseOnlyImplementedMetadataCollectionsGivenGetSchemaWhenCalled()
        {
            // Arrange
            using CassieConnection connection = new();

            // Act
            DataTable schema = connection.GetSchema();
            string[] collections = [.. schema.Rows.Cast<DataRow>().Select(row => row.Field<string>("CollectionName")!)];

            // Assert
            Assert.Equal(["MetaDataCollections", "Restrictions", "Tables", "Columns"], collections);
            Assert.Equal(4, schema.Rows.Cast<DataRow>().Single(row => row.Field<string>("CollectionName") == "Tables").Field<int>("NumberOfRestrictions"));
            Assert.Equal(3, schema.Rows.Cast<DataRow>().Single(row => row.Field<string>("CollectionName") == "Tables").Field<int>("NumberOfIdentifierParts"));
        }

        [Fact]
        public void ShouldRejectUnsupportedSchemaCollectionGivenGetSchemaWhenCalled()
        {
            // Arrange
            using CassieConnection connection = new();

            // Act
            ArgumentException exception = Assert.Throws<ArgumentException>(() => connection.GetSchema("Indexes"));

            // Assert
            Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRejectExcessSchemaRestrictionsGivenKnownCollectionWhenCalled()
        {
            // Arrange
            using CassieConnection connection = new();

            // Act
            ArgumentException exception = Assert.Throws<ArgumentException>(() => connection.GetSchema("Tables", [null, null, null, null, "extra"]));

            // Assert
            Assert.Contains("at most 4 restrictions", exception.Message, StringComparison.Ordinal);
        }
    }
}
