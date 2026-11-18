using System.Reflection;
using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Href.Core.Tests.Common;

public sealed class HealthControllerTests
{
    [Fact]
    public async Task Get_WhenDatabaseAnswers_Returns200Healthy()
    {
        var session = FakeSession.Returning(1);
        var controller = new HealthController(new DatabaseHealthService(session, NullLogger<DatabaseHealthService>.Instance));

        var actionResult = await controller.Get(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
        Assert.Equal(new HealthController.HealthResponse("Healthy", "Healthy"), okResult.Value);
    }

    [Fact]
    public async Task Get_WhenDatabaseThrows_Returns503Unhealthy()
    {
        var session = FakeSession.Throwing(new InvalidOperationException("Database unavailable"));
        var controller = new HealthController(new DatabaseHealthService(session, NullLogger<DatabaseHealthService>.Instance));

        var actionResult = await controller.Get(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        Assert.Equal(new HealthController.HealthResponse("Unhealthy", "Unhealthy"), objectResult.Value);
    }

    [Fact]
    public async Task Get_WhenCancelled_PropagatesOperationCanceledException()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var session = FakeSession.Throwing(new OperationCanceledException(cancellationTokenSource.Token));
        var controller = new HealthController(new DatabaseHealthService(session, NullLogger<DatabaseHealthService>.Instance));

        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Get(cancellationTokenSource.Token));
    }

    public class FakeSession : DispatchProxy
    {
        private Func<Task<object>> _handler = () => Task.FromResult<object>(1);

        public static ISession Returning(object result) =>
            Create(() => Task.FromResult(result));

        public static ISession Throwing(Exception exception) =>
            Create(() => Task.FromException<object>(exception));

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISession.CreateSQLQuery))
            {
                return FakeSqlQuery.Create(_handler);
            }

            throw new NotSupportedException($"Unexpected call to {targetMethod?.Name}.");
        }

        private static ISession Create(Func<Task<object>> handler)
        {
            var proxy = Create<ISession, FakeSession>();
            ((FakeSession)(object)proxy)._handler = handler;
            return proxy;
        }
    }

    public class FakeSqlQuery : DispatchProxy
    {
        private Func<Task<object>> _handler = () => Task.FromResult<object>(1);

        public static ISQLQuery Create(Func<Task<object>> handler)
        {
            var proxy = Create<ISQLQuery, FakeSqlQuery>();
            ((FakeSqlQuery)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISQLQuery.UniqueResultAsync))
            {
                return _handler();
            }

            throw new NotSupportedException($"Unexpected call to {targetMethod?.Name}.");
        }
    }
}
