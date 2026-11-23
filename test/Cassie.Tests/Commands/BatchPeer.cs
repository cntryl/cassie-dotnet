using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Cntryl.Cassie.Tests.Commands;

/// <summary>One frontend frame received by a scripted batch peer.</summary>
internal sealed record BatchFrame(byte Tag, byte[] Payload);

/// <summary>Records what a scripted peer received and answers extended-protocol batches.</summary>
internal sealed class BatchPeer
{
    private readonly ConcurrentQueue<BatchFrame[]> _batches = new();
    private readonly ConcurrentQueue<string> _simpleQueries = new();

    /// <summary>Gets the frames of each batch, recorded when its Sync arrived and before any reply was written.</summary>
    internal BatchFrame[][] Batches => [.. _batches];

    /// <summary>Gets the simple queries received, in order.</summary>
    internal string[] SimpleQueries => [.. _simpleQueries];

    /// <summary>
    ///     Serves sessions: each batch ends at Sync and is answered by <paramref name="respond" /> (called with the
    ///     1-based batch number).
    /// </summary>
    internal Func<ScriptedSession, Task> Serve(Func<int, ScriptedSession, Task> respond)
    {
        return async session =>
        {
            List<BatchFrame> pending = [];
            var inTransaction = false;
            var batchNumber = 0;

            while (true)
            {
                var (tag, payload) = await session.ReadFrameAsync();

                if (tag == (byte)'X')
                {
                    return;
                }


                if (tag == (byte)'S')
                {
                    batchNumber++;
                    pending.Add(new BatchFrame(tag, payload));
                    _batches.Enqueue([.. pending]);
                    pending.Clear();
                    await respond(batchNumber, session);
                    await session.WriteReadyAsync(inTransaction ? 'T' : 'I');
                }
                else if (tag == (byte)'Q')
                {
                    var sql = Encoding.UTF8.GetString(payload, 0, payload.Length - 1);
                    _simpleQueries.Enqueue(sql);

                    if (sql.StartsWith("BEGIN", StringComparison.Ordinal))
                    {
                        inTransaction = true;
                        await session.WriteCommandCompleteAsync("BEGIN");
                    }
                    else if (sql is "ROLLBACK" or "COMMIT")
                    {
                        inTransaction = false;
                        await session.WriteCommandCompleteAsync(sql);
                    }
                    else
                    {
                        await session.WriteIntRowDescriptionAsync();
                        await session.WriteTextRowAsync("7");
                        await session.WriteCommandCompleteAsync("SELECT 1");
                    }


                    await session.WriteReadyAsync(inTransaction ? 'T' : 'I');
                }
                else
                {
                    pending.Add(new BatchFrame(tag, payload));
                }
            }
        };
    }

    /// <summary>Writes ParseComplete, BindComplete, a one-column int result, and CommandComplete.</summary>
    internal static async Task ReplyWithRowsAsync(ScriptedSession session, params string[] values)
    {
        await session.WriteAsync((byte)'1', []);
        await session.WriteAsync((byte)'2', []);
        await session.WriteIntRowDescriptionAsync();

        foreach (var value in values)
        {
            await session.WriteTextRowAsync(value);
        }


        await session.WriteCommandCompleteAsync("SELECT " + values.Length.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes ParseComplete, BindComplete, NoData, and CommandComplete for a statement that returns no rows.</summary>
    internal static async Task ReplyWithCommandAsync(ScriptedSession session, string tag)
    {
        await session.WriteAsync((byte)'1', []);
        await session.WriteAsync((byte)'2', []);
        await session.WriteAsync((byte)'n', []);
        await session.WriteCommandCompleteAsync(tag);
    }

    /// <summary>Writes ParseComplete, BindComplete, NoData, and EmptyQueryResponse.</summary>
    internal static async Task ReplyWithEmptyQueryAsync(ScriptedSession session)
    {
        await session.WriteAsync((byte)'1', []);
        await session.WriteAsync((byte)'2', []);
        await session.WriteAsync((byte)'n', []);
        await session.WriteAsync((byte)'I', []);
    }

    internal static Task ReplyWithErrorAsync(ScriptedSession session, string sqlState, string message)
    {
        byte[] error =
        [
            (byte)'S', .. "ERROR\0"u8, (byte)'C', .. Encoding.UTF8.GetBytes(sqlState), 0, (byte)'M',
            .. Encoding.UTF8.GetBytes(message), 0, 0
        ];
        return session.WriteAsync((byte)'E', error);
    }

    /// <summary>
    ///     Answers each received frame the way a server would: ParseComplete for Parse, ParameterDescription and
    ///     NoData for a statement Describe, BindComplete for Bind, NoData for a portal Describe, CommandComplete
    ///     for Execute, and CloseComplete for Close.
    /// </summary>
    internal static async Task ReplyInKindAsync(ScriptedSession session, BatchFrame[] received, string commandTag)
    {
        foreach (var frame in received)
        {
            switch ((char)frame.Tag)
            {
                case 'P':
                    await session.WriteAsync((byte)'1', []);
                    break;
                case 'B':
                    await session.WriteAsync((byte)'2', []);
                    break;
                case 'D' when frame.Payload[0] == (byte)'S':
                    await session.WriteAsync((byte)'t', [0, 0]);
                    await session.WriteAsync((byte)'n', []);
                    break;
                case 'D':
                    await session.WriteAsync((byte)'n', []);
                    break;
                case 'E':
                    await session.WriteCommandCompleteAsync(commandTag);
                    break;
                case 'C':
                    await session.WriteAsync((byte)'3', []);
                    break;
            }
        }
    }

    /// <summary>Returns the statement name a Parse frame defines.</summary>
    internal static string ParseStatementName(BatchFrame parse)
    {
        return Encoding.UTF8.GetString(parse.Payload, 0, Array.IndexOf(parse.Payload, (byte)0));
    }

    /// <summary>Returns the statement name a Bind frame executes.</summary>
    internal static string BindStatementName(BatchFrame bind)
    {
        var portalEnd = Array.IndexOf(bind.Payload, (byte)0);
        var statementEnd = Array.IndexOf(bind.Payload, (byte)0, portalEnd + 1);
        return Encoding.UTF8.GetString(bind.Payload, portalEnd + 1, statementEnd - portalEnd - 1);
    }

    /// <summary>Returns the frame tags of a batch as text, for example <c>PBDEPBDES</c>.</summary>
    internal static string Tags(BatchFrame[] batch)
    {
        return new string([.. batch.Select(static frame => (char)frame.Tag)]);
    }

    /// <summary>Returns the SQL text of a Parse frame.</summary>
    internal static string ParseSql(BatchFrame parse)
    {
        var nameEnd = Array.IndexOf(parse.Payload, (byte)0);
        var sqlEnd = Array.IndexOf(parse.Payload, (byte)0, nameEnd + 1);
        return Encoding.UTF8.GetString(parse.Payload, nameEnd + 1, sqlEnd - nameEnd - 1);
    }
}
