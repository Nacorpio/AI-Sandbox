using AISandbox.Application.Features.Runs;
using AISandbox.Domain.Experimentation;

namespace AISandbox.Application.Tests.Runs;

public sealed class AnswerMatrixTests
{
    private static ChoiceAnswer Choice(string choice) => new(choice, 0.9, new Dictionary<string, double> { [choice] = 1 });

    private static ScoreAnswer Score(double score) => new(score, 0.9, new Dictionary<string, string>(), new Dictionary<string, double>());

    [Fact]
    public void Same_choice_agrees_and_different_choices_disagree()
    {
        Assert.False(AnswerMatrix.Disagree([Choice("billing"), Choice("billing")]));
        Assert.True(AnswerMatrix.Disagree([Choice("billing"), Choice("sales")]));
    }

    [Fact]
    public void Scores_that_round_to_the_same_level_agree()
    {
        Assert.False(AnswerMatrix.Disagree([Score(1.2), Score(0.8)]));
        Assert.True(AnswerMatrix.Disagree([Score(1.2), Score(1.6)]));
    }

    [Fact]
    public void Noul_answers_disagree_only_across_one_half()
    {
        Assert.False(AnswerMatrix.Disagree([new NoulAnswer(0.5), new NoulAnswer(0.99)]));
        Assert.False(AnswerMatrix.Disagree([new NoulAnswer(0.1), new NoulAnswer(0.49)]));
        Assert.True(AnswerMatrix.Disagree([new NoulAnswer(0.49), new NoulAnswer(0.5)]));
    }

    [Fact]
    public void A_missing_answer_is_not_a_disagreement()
    {
        Assert.False(AnswerMatrix.Disagree([Choice("billing"), null]));
        Assert.True(AnswerMatrix.Disagree([Choice("billing"), null, Choice("sales")]));
    }

    private static ExecutionView Execution(string name, ExecutionStatus status, params (string Key, Answer Answer)[] answers) => new(
        ExecutionId.New(), name, "systemone", "m", status,
        status == ExecutionStatus.Succeeded ? new DecisionOutput(answers.ToDictionary(a => a.Key, a => a.Answer)) : null,
        null, null, null, null, null, null, null, null);

    private static RunView Run(params ExecutionView[] executions) => new(
        RunId.New(), DateTimeOffset.UtcNow, RunStatus.Completed, "state",
        [new QuestionView("dept", "Choice", "Which team"), new QuestionView("urgent", "Noul", "Urgent")],
        executions);

    [Fact]
    public void Matrix_needs_two_successful_models()
    {
        var one = Run(Execution("A", ExecutionStatus.Succeeded, ("dept", Choice("x"))), Execution("B", ExecutionStatus.Failed));

        Assert.Null(AnswerMatrix.Of(one));
    }

    [Fact]
    public void Matrix_has_a_row_per_question_and_a_column_per_successful_model()
    {
        var run = Run(
            Execution("A", ExecutionStatus.Succeeded, ("dept", Choice("billing")), ("urgent", new NoulAnswer(0.9))),
            Execution("Broken", ExecutionStatus.Failed),
            Execution("B", ExecutionStatus.Succeeded, ("dept", Choice("sales")), ("urgent", new NoulAnswer(0.8))));

        var matrix = AnswerMatrix.Of(run)!;

        Assert.Equal(["A", "B"], matrix.Columns.Select(c => c.ModelName));
        Assert.Equal(["dept", "urgent"], matrix.Rows.Select(r => r.Question.Key));
        Assert.True(matrix.Rows[0].Disagree);
        Assert.False(matrix.Rows[1].Disagree);
        Assert.Equal("sales", Assert.IsType<ChoiceAnswer>(matrix.Rows[0].Answers[1]).Choice);
    }
}
