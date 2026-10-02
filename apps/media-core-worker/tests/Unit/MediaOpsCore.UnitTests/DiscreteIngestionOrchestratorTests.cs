using MediaOpsCore.Modules.Capture.Application;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class DiscreteIngestionOrchestratorTests
{
    [Fact]
    public async Task ExecuteCycleAsync_does_nothing_when_no_discrete_plugin_is_configured()
    {
        var sut = new DiscreteIngestionOrchestrator(NullLogger<DiscreteIngestionOrchestrator>.Instance);

        await sut.ExecuteCycleAsync();
    }

    [Fact]
    public async Task ExecuteCycleAsync_delegates_to_the_web_news_use_case_when_configured()
    {
        var useCase = new FakeWebNewsCaptureUseCase();
        var sut = new DiscreteIngestionOrchestrator(NullLogger<DiscreteIngestionOrchestrator>.Instance, useCase);

        await sut.ExecuteCycleAsync();

        Assert.True(useCase.WasCalled);
    }

    private sealed class FakeWebNewsCaptureUseCase : IWebNewsCaptureUseCase
    {
        public bool WasCalled { get; private set; }

        public Task ExecuteCycleAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }
}
