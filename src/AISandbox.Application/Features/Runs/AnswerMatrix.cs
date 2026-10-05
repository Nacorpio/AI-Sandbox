using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Features.Runs;

/// <param name="Question">The question this row compares.</param>
/// <param name="Answers">One entry per matrix column, in column order; null when that model gave no answer.</param>
/// <param name="Disagree">True when the models that answered do not agree.</param>
public sealed record MatrixRow(QuestionView Question, IReadOnlyList<Answer?> Answers, bool Disagree);

/// <summary>
/// Rows are questions, columns are the models that succeeded.
/// </summary>
public sealed record AnswerMatrix(IReadOnlyList<ExecutionView> Columns, IReadOnlyList<MatrixRow> Rows)
{
    /// <summary>
    /// The comparison of a run, or null while fewer than two models have succeeded.
    /// </summary>
    public static AnswerMatrix? Of(RunView run)
    {
        var columns = run.Executions
            .Where(e => e.Status == ExecutionStatus.Succeeded && e.Output is DecisionOutput)
            .ToList();
        if (columns.Count < 2)
        {
            return null;
        }

        var rows = run.Questions.Select(question =>
        {
            var answers = columns
                .Select(c => ((DecisionOutput)c.Output!).Answers.GetValueOrDefault(question.Key))
                .ToList();
            return new MatrixRow(question, answers, Disagree(answers));
        }).ToList();
        return new AnswerMatrix(columns, rows);
    }

    /// <summary>
    /// Choice: different choices. Score: different rounded levels. Noul: one side at or above 0.5
    /// and the other below. Missing answers do not count.
    /// </summary>
    public static bool Disagree(IEnumerable<Answer?> answers) =>
        answers
            .OfType<Answer>()
            .Select(Position)
            .Distinct()
            .Count() > 1;

    private static string Position(Answer answer) => answer switch
    {
        ChoiceAnswer choice => $"choice:{choice.Choice}",
        ScoreAnswer score => $"level:{Level(score)}",
        NoulAnswer noul => $"noul:{noul.Value >= 0.5}",
        _ => answer.GetType().Name,
    };

    /// <summary>
    /// The rubric level a score falls on.
    /// </summary>
    public static int Level(ScoreAnswer score) => (int)Math.Round(score.Score, MidpointRounding.AwayFromZero);
}
