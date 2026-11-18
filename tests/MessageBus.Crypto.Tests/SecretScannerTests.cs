using System.Text.Json;
using MessageBus.Contracts.Messaging;
using Xunit;

namespace MessageBus.Crypto.Tests;

/// <summary>The send-time scan blocks obvious credentials and lets ordinary task text through.</summary>
public class SecretScannerTests
{
    [Theory]
    [InlineData("private-key", "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("private-key", "-----BEGIN PRIVATE KEY-----")]
    [InlineData("aws-access-key-id", "aws_access_key_id = AKIAIOSFODNN7EXAMPLE")]
    [InlineData("github-token", "export GH_TOKEN=ghp_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("github-token", "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS")]
    [InlineData("azure-storage-key", "DefaultEndpointsProtocol=https;AccountName=x;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;")]
    [InlineData("anthropic-api-key", "ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("openai-api-key", "OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuvwxyz0123456789ABCD")]
    [InlineData("slack-token", "xoxb-123456789012-1234567890123-AbCdEfGhIjKlMnOpQrStUvWx")]
    [InlineData("jwt", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    public void Flags_well_known_credential_shapes(string kind, string text)
    {
        var finding = Assert.Single(SecretScanner.Scan($"Here is the config:\n{text}\nthanks"), f => f.Kind == kind);
        Assert.DoesNotContain(text[8..], finding.Preview);
    }

    [Theory]
    [InlineData("Add a due date column to the todo table and expose it on GET /todos.")]
    [InlineData("Use the AWS SDK; credentials come from the runner's environment, never the message.")]
    [InlineData("Rename ghp_token to githubToken in Settings.cs")]
    [InlineData("See https://github.com/acme/todo/pull/42 and commit a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0")]
    [InlineData("-----BEGIN PUBLIC KEY----- is fine to share")]
    public void Lets_ordinary_task_text_through(string text) =>
        Assert.Empty(SecretScanner.Scan(text));

    [Fact]
    public void Looks_in_every_part_of_a_body()
    {
        var body = new MessageBody
        {
            Summary = "Deploy the preview",
            Data = JsonDocument.Parse("""{"env":{"KEY":"AKIAIOSFODNN7EXAMPLE"}}""").RootElement,
        };

        Assert.Equal("aws-access-key-id", Assert.Single(SecretScanner.Scan(body)).Kind);
        var friction = body with { Data = null, Friction = [new FrictionNote { Kind = "other", Note = "AKIAIOSFODNN7EXAMPLE" }] };
        Assert.Equal("aws-access-key-id", Assert.Single(SecretScanner.Scan(friction)).Kind);
    }
}
