using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    public sealed class DateStyleTrackerTests
    {
        [Theory]
        [InlineData("SELECT 'set_config'", false)]
        [InlineData("SELECT $$set_config('DateStyle')$$", false)]
        [InlineData("SELECT 1 -- set_config('DateStyle')", false)]
        [InlineData("SELECT 1 /* outer /* set_config('DateStyle') */ comment */", false)]
        [InlineData("SELECT set_config_status", false)]
        [InlineData("SELECT @set_config", false)]
        [InlineData("SELECT set_config", false)]
        [InlineData("SELECT SET_CONFIG /* comment */ ('DateStyle', 'ISO', false)", true)]
        [InlineData("SELECT pg_catalog.\"set_config\"('DateStyle', 'ISO', false)", true)]
        [InlineData("SELECT \"SET_CONFIG\"('DateStyle', 'ISO', false)", false)]
        [InlineData("SELECT \"set_config\"", false)]
        public void ShouldRecognizeFunctionCallGivenLexicalContextWhenSqlIsObserved(string sql, bool needsRefresh)
        {
            // Arrange
            DateStyleTracker tracker = new();

            // Act
            tracker.ObserveSql(sql);

            // Assert
            Assert.Equal(needsRefresh, tracker.NeedsRefresh);
        }

        [Fact]
        public void ShouldCaptureStartupStyleGivenServerParameterWhenStartupCompletes()
        {
            // Arrange
            DateStyleTracker tracker = new();
            tracker.ApplyParameterStatus("datestyle", "SQL, DMY");

            // Act
            tracker.CompleteStartup();

            // Assert
            Assert.Equal(new CassieDateStyle(CassieDateOutput.Sql, CassieDateOrder.Dmy), tracker.Current);
            Assert.Equal(tracker.Current, tracker.Startup);
            Assert.False(tracker.NeedsRefresh);
        }

        [Fact]
        public void ShouldIgnoreUnrelatedStatusGivenKnownStyleWhenParameterIsObserved()
        {
            // Arrange
            DateStyleTracker tracker = new();
            CassieDateStyle? prior = tracker.Current;

            // Act
            tracker.ApplyParameterStatus("TimeZone", "UTC");

            // Assert
            Assert.Equal(prior, tracker.Current);
        }

        [Fact]
        public void ShouldInvalidateCurrentStyleGivenUnsupportedStatusWhenParameterIsObserved()
        {
            // Arrange
            DateStyleTracker tracker = new();

            // Act
            tracker.ApplyParameterStatus("DateStyle", "unknown");
            tracker.CompleteStartup();

            // Assert
            Assert.Null(tracker.Current);
            Assert.Equal(CassieDateStyle.Default, tracker.Startup);
        }

        [Theory]
        [InlineData("SET")]
        [InlineData("RESET")]
        public void ShouldRequireRefreshGivenSettingCommandWhenCommandCompletes(string text)
        {
            // Arrange
            DateStyleTracker tracker = new();

            // Act
            tracker.ObserveCommandComplete(new CommandTag(text));

            // Assert
            Assert.Null(tracker.Current);
            Assert.True(tracker.NeedsRefresh);
            Assert.True(tracker.CanRefresh);
        }

        [Theory]
        [InlineData("SELECT set_config('DateStyle', 'SQL, DMY', false)", true)]
        [InlineData("SELECT SET_CONFIG('DateStyle', 'SQL, DMY', false)", true)]
        [InlineData("SELECT 1", false)]
        public void ShouldTrackPotentialSettingChangesGivenSqlWhenTextIsObserved(string sql, bool needsRefresh)
        {
            // Arrange
            DateStyleTracker tracker = new();

            // Act
            tracker.ObserveSql(sql);

            // Assert
            Assert.Equal(needsRefresh, tracker.NeedsRefresh);
        }

        [Theory]
        [InlineData("COMMIT")]
        [InlineData("END")]
        [InlineData("ROLLBACK")]
        [InlineData("ABORT")]
        public void ShouldRecheckStyleGivenTransactionChangedSettingsWhenTransactionEnds(string text)
        {
            // Arrange
            DateStyleTracker tracker = new();
            _ = tracker.TryObserveReadyForQuery([TransactionStatus.InTransaction]);
            tracker.NoteSettingChange();
            tracker.ApplyParameterStatus("DateStyle", "SQL, DMY");

            // Act
            tracker.ObserveCommandComplete(new CommandTag(text));

            // Assert
            Assert.Null(tracker.Current);
            Assert.True(tracker.NeedsRefresh);
        }

        [Fact]
        public void ShouldForgetPriorTransactionChangesGivenNewTransactionWhenUnchangedTransactionEnds()
        {
            // Arrange
            DateStyleTracker tracker = new();
            _ = tracker.TryObserveReadyForQuery([TransactionStatus.InTransaction]);
            tracker.NoteSettingChange();
            _ = tracker.TryObserveReadyForQuery([TransactionStatus.Idle]);
            tracker.ApplyParameterStatus("DateStyle", "ISO, MDY");
            _ = tracker.TryObserveReadyForQuery([TransactionStatus.InTransaction]);

            // Act
            tracker.ObserveCommandComplete(new CommandTag("COMMIT"));

            // Assert
            Assert.Equal(CassieDateStyle.Default, tracker.Current);
            Assert.False(tracker.NeedsRefresh);
        }

        [Fact]
        public void ShouldDeferRefreshGivenFailedTransactionWhenSettingChangeIsPending()
        {
            // Arrange
            DateStyleTracker tracker = new();
            tracker.NoteSettingChange();

            // Act
            bool observed = tracker.TryObserveReadyForQuery([TransactionStatus.Failed]);

            // Assert
            Assert.True(observed);
            Assert.True(tracker.NeedsRefresh);
            Assert.False(tracker.CanRefresh);
        }

        [Theory]
        [InlineData("")]
        [InlineData("49FF")]
        [InlineData("58")]
        public void ShouldPreserveTransactionStatusGivenMalformedReadyPayloadWhenStatusIsObserved(string hex)
        {
            // Arrange
            DateStyleTracker tracker = new();
            _ = tracker.TryObserveReadyForQuery([TransactionStatus.InTransaction]);

            // Act
            bool observed = tracker.TryObserveReadyForQuery(Convert.FromHexString(hex));

            // Assert
            Assert.False(observed);
            Assert.Equal(TransactionStatus.InTransaction, tracker.TransactionStatus);
        }

        [Fact]
        public void ShouldPreventRecursiveRefreshGivenPendingStyleWhenRefreshBeginsAndEnds()
        {
            // Arrange
            DateStyleTracker tracker = new();
            tracker.NoteSettingChange();

            // Act
            tracker.BeginRefresh();
            bool canRefreshDuringRefresh = tracker.CanRefresh;
            tracker.EndRefresh();

            // Assert
            Assert.False(canRefreshDuringRefresh);
            Assert.True(tracker.CanRefresh);
        }

        [Fact]
        public void ShouldRestoreStartupStyleGivenChangedSettingsWhenDiscardAllCompletes()
        {
            // Arrange
            DateStyleTracker tracker = new();
            tracker.ApplyParameterStatus("DateStyle", "SQL, DMY");
            tracker.CompleteStartup();
            tracker.ApplyParameterStatus("DateStyle", "German, DMY");
            tracker.NoteSettingChange();

            // Act
            tracker.ObserveCommandComplete(new CommandTag("DISCARD ALL"));

            // Assert
            Assert.Equal(tracker.Startup, tracker.Current);
            Assert.False(tracker.NeedsRefresh);
        }

        [Fact]
        public void ShouldClearLeaseStateGivenPendingChangeAndFailedTransactionWhenSessionIsLeased()
        {
            // Arrange
            DateStyleTracker tracker = new();
            tracker.NoteSettingChange();
            _ = tracker.TryObserveReadyForQuery([TransactionStatus.Failed]);

            // Act
            tracker.BeginLease();

            // Assert
            Assert.Equal(TransactionStatus.Idle, tracker.TransactionStatus);
            Assert.False(tracker.NeedsRefresh);
        }
    }
}
