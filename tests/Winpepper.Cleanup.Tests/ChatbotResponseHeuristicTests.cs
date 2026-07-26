using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>
/// Pure unit tests for <see cref="ChatbotResponseHeuristic"/>. These run on
/// every platform with no model, so the heuristic itself is regression-tested
/// even where the model-gated eval suite skips.
/// </summary>
public class ChatbotResponseHeuristicTests
{
    [Fact]
    public void CleanedQuestionPassthrough_IsNotChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "what is a synonym for whisper",
            "What is a synonym for whisper?").ShouldBeFalse();
    }

    [Fact]
    public void SurePrefixAnswer_IsChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "What is a synonym for whisper?",
            "Sure, a synonym for whisper is murmur.").ShouldBeTrue();
    }

    [Fact]
    public void HeresPrefixAnswer_IsChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "Tell me a joke about programming.",
            "Here's one: why do programmers prefer dark mode?").ShouldBeTrue();
    }

    [Fact]
    public void HeresPrefixWithTypographicApostrophe_IsChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "Tell me a joke about programming.",
            "Here\u2019s a good one about programming.").ShouldBeTrue();
    }

    [Fact]
    public void DictatedSureOpening_IsNotChatbot()
    {
        // The user themselves dictated "sure ..." - the opener guard must not fire.
        ChatbotResponseHeuristic.IsChatbotResponse(
            "sure sounds good let's meet at noon",
            "Sure, sounds good. Let's meet at noon.").ShouldBeFalse();
    }

    [Fact]
    public void SurelyWordBoundary_IsNotChatbot()
    {
        // "Surely" must not match the "sure" opener.
        ChatbotResponseHeuristic.IsChatbotResponse(
            "surely the plan works as described",
            "Surely the plan works as described.").ShouldBeFalse();
    }

    [Fact]
    public void AsAnAiAnywhere_IsChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "Can you help me write an email to my boss?",
            "Unfortunately, as an AI I cannot send emails for you.").ShouldBeTrue();
    }

    [Fact]
    public void LengthBlowup_IsChatbot()
    {
        var ramble = string.Join(" ", Enumerable.Repeat("programming jokes are a classic staple of office humor", 6));
        ChatbotResponseHeuristic.IsChatbotResponse(
            "Tell me a joke about programming.", ramble).ShouldBeTrue();
    }

    [Fact]
    public void SpuriousNumberedList_IsChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "Create a todo list for my week.",
            "1. Buy groceries\n2. Call the dentist").ShouldBeTrue();
    }

    [Fact]
    public void SpuriousBulletedList_IsChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "Create a todo list for my week.",
            "- buy groceries\n- call the dentist").ShouldBeTrue();
    }

    [Fact]
    public void InputAlreadyContainsList_ListOutputIsNotChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "1. buy groceries\n2. call the dentist about the appointment",
            "1. Buy groceries.\n2. Call the dentist about the appointment.").ShouldBeFalse();
    }

    [Fact]
    public void EmptyOutput_IsNotChatbot()
    {
        // Empty output is the runner's fallback concern, not the heuristic's.
        ChatbotResponseHeuristic.IsChatbotResponse("anything at all here", "  ").ShouldBeFalse();
    }

    [Fact]
    public void OrdinaryFillerCleanup_IsNotChatbot()
    {
        ChatbotResponseHeuristic.IsChatbotResponse(
            "so um the meeting is like at 3pm you know on tuesday afternoon",
            "The meeting is at 3pm on Tuesday afternoon.").ShouldBeFalse();
    }
}
