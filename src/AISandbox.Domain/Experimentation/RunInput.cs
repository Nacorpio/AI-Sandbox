using AISandbox.Domain.Abstractions;
using AISandbox.Domain.Authoring.QuestionSets;
using AISandbox.Domain.Authoring.Questions;
using AISandbox.Domain.Catalog.Models;

namespace AISandbox.Domain.Experimentation;

/// <summary>
/// Immutable snapshot of what a run sends. Decision models get the state and questions; chat
/// models get the prompt. A run may hold both, and each execution uses what its model needs.
/// </summary>
public sealed record RunInput
{
    public const int MaxQuestions = QuestionRules.MaxQuestions;

    private static readonly IReadOnlyDictionary<ModelDefinitionId, ChatOptions> NoOptions =
        new Dictionary<ModelDefinitionId, ChatOptions>();

    private RunInput(
        StructuredText? state,
        IReadOnlyList<Question> questions,
        QuestionSetRef? questionSet,
        ChatPrompt? chat,
        IReadOnlyDictionary<ModelDefinitionId, ChatOptions> chatOptions)
    {
        State = state;
        Questions = questions;
        QuestionSet = questionSet;
        Chat = chat;
        ChatOptions = chatOptions;
    }

    /// <summary>
    /// The content decision models evaluate. Null for a run that only asks chat models.
    /// </summary>
    public StructuredText? State { get; }

    public IReadOnlyList<Question> Questions { get; }

    /// <summary>
    /// The saved question set (and version) the questions came from, when they came from one.
    /// </summary>
    public QuestionSetRef? QuestionSet { get; }

    /// <summary>
    /// The prompt chat models answer.
    /// </summary>
    public ChatPrompt? Chat { get; }

    /// <summary>
    /// Chat options chosen for each chat model of the run.
    /// </summary>
    public IReadOnlyDictionary<ModelDefinitionId, ChatOptions> ChatOptions { get; }

    public bool HasDecisionInput => State is not null;

    public ChatOptions ChatOptionsFor(ModelDefinitionId model) =>
        ChatOptions.TryGetValue(model, out var options) ? options : new ChatOptions();

    /// <summary>
    /// A questions-based input: the state plus between 1 and <see cref="MaxQuestions"/> questions.
    /// </summary>
    public static Result<RunInput> Create(StructuredText state, IReadOnlyList<Question> questions, QuestionSetRef? questionSet = null) =>
        Create(state, questions, questionSet, null, null);

    /// <summary>
    /// A prompt-based input for chat models.
    /// </summary>
    public static Result<RunInput> CreateChat(ChatPrompt chat, IReadOnlyDictionary<ModelDefinitionId, ChatOptions>? options = null) =>
        Create(null, [], null, chat, options);

    /// <summary>
    /// An input that holds a decision part, a chat part, or both. At least one is required;
    /// a decision part needs a state and valid questions.
    /// </summary>
    public static Result<RunInput> Create(
        StructuredText? state,
        IReadOnlyList<Question> questions,
        QuestionSetRef? questionSet,
        ChatPrompt? chat,
        IReadOnlyDictionary<ModelDefinitionId, ChatOptions>? chatOptions)
    {
        if (state is null && questions.Count == 0 && chat is null)
        {
            return Error.Validation("input", "Provide questions for decision models or a prompt for chat models.");
        }

        if (state is not null || questions.Count > 0)
        {
            if (state is null)
            {
                return Error.Validation("state", "State is required.");
            }

            if (QuestionRules.Check(questions) is { } error)
            {
                return error;
            }
        }

        if (chat is not null && string.IsNullOrWhiteSpace(chat.User))
        {
            return Error.Validation("prompt", "The user prompt is required.");
        }

        return new RunInput(state, questions, questionSet, chat, chatOptions ?? NoOptions);
    }
}
