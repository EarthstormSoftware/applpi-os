using System.Text;
using System.Text.Json;

namespace Juggle.Core.Jobs;

/// <summary>
/// How a card job with administrator rights talks to the juggle (or Juggle Flash) that started it, over a named pipe
/// (Windows) or its standard input and output (Linux, through sudo or pkexec): one line of JSON each way for the
/// request, then a line for each thing it reports, ending with how it ended. The settings' secrets go only this way,
/// never through a file or the command line.
/// </summary>
public static class CardJobStream
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A line from the job: say, problem or progress (Text or Progress), or end (Outcome).</summary>
    sealed record Message(string Kind, string? Text = null, JobProgress? Progress = null, CardOutcome? Outcome = null);

    /// <summary>The job's side: reads the request, runs it, and reports back. Returns how it ended.</summary>
    public static CardOutcome Serve(Stream input, Stream output, ICards? cards = null)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var report = new StreamReport(writer);
        CardOutcome outcome;
        try
        {
            var request = JsonSerializer.Deserialize<CardRequest>(
                reader.ReadLine() ?? throw new IOException("juggle sent no card to write."), Json)
                ?? throw new IOException("juggle sent no card to write.");
            outcome = new CardJob(report, cards).Run(request);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or JsonException)
        {
            report.Progress(null);
            report.Problem(error.Message);
            outcome = CardOutcome.Failed;
        }
        report.Send(new Message("end", Outcome: outcome));
        return outcome;
    }

    /// <summary>
    /// The side that started the job: sends the request, then passes on what the job reports until it ends. Returns
    /// how it ended, or null if it stopped without saying.
    /// </summary>
    public static CardOutcome? Relay(CardRequest request, Stream toJob, Stream fromJob, IJobReport report)
    {
        using (var writer = new StreamWriter(toJob, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.WriteLine(JsonSerializer.Serialize(request, Json));
        }
        toJob.Flush();
        using var reader = new StreamReader(fromJob, Encoding.UTF8, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            var message = JsonSerializer.Deserialize<Message>(line, Json);
            switch (message?.Kind)
            {
                case "say":
                    report.Say(message.Text ?? "");
                    break;
                case "problem":
                    report.Problem(message.Text ?? "");
                    break;
                case "progress":
                    report.Progress(message.Progress);
                    break;
                case "end":
                    return message.Outcome ?? CardOutcome.Failed;
            }
        }
        report.Progress(null);
        return null;
    }

    sealed class StreamReport(StreamWriter writer) : IJobReport
    {
        public void Say(string text) => Send(new Message("say", text));

        public void Problem(string text) => Send(new Message("problem", text));

        public void Progress(JobProgress? progress) => Send(new Message("progress", Progress: progress));

        public void Send(Message message)
        {
            lock (writer)
            {
                writer.WriteLine(JsonSerializer.Serialize(message, Json));
            }
        }
    }
}
