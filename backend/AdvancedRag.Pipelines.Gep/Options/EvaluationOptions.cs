namespace AdvancedRag.Pipelines.Gep;

/// <summary>Where the evaluation cases live and how a run is paced.</summary>
public sealed class EvaluationOptions
{
    /// <summary>JSON file with questions and structural expectations, inside the shared eval folder.</summary>
    public string CasesFile { get; set; } = "gep-cases.json";

    /// <summary>Latest report, written to the shared eval/results folder.</summary>
    public string ResultsFileName { get; set; } = "gep-latest.json";

    /// <summary>Pause between questions so a run stays well inside the Gemini per-minute quota.</summary>
    public int DelayBetweenCasesMilliseconds { get; set; } = 2000;

    /// <summary>Upper limit for repeat (each repeat asks every case again; answers can vary run to run).</summary>
    public int MaxRepeat { get; set; } = 3;
}
