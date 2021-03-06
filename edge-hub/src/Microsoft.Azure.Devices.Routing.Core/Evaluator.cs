// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Routing.Core
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Concurrency;
    using Microsoft.Azure.Devices.Routing.Core.MessageSources;
    using Microsoft.Azure.Devices.Routing.Core.Query;
    using Microsoft.Extensions.Logging;
    using static System.FormattableString;

    public class Evaluator
    {
        readonly object sync = new object();

        // ReSharper disable once InconsistentlySynchronizedField - compiledRoutes is immutable
        readonly AtomicReference<ImmutableDictionary<string, CompiledRoute>> compiledRoutes;
        readonly IRouteCompiler compiler;
        readonly Option<CompiledRoute> fallback;

        public Evaluator(RouterConfig config)
            : this(config, RouteCompiler.Instance)
        {
        }

        public Evaluator(RouterConfig config, IRouteCompiler compiler)
        {
            Preconditions.CheckNotNull(config, nameof(config));
            this.compiler = Preconditions.CheckNotNull(compiler);
            this.fallback = config.Fallback.Map(this.Compile);

            ImmutableDictionary<string, CompiledRoute> routesDict = config
                .Routes
                .ToImmutableDictionary(r => r.Id, r => this.Compile(r));
            this.compiledRoutes = new AtomicReference<ImmutableDictionary<string, CompiledRoute>>(routesDict);
        }

        // Because we are only reading here, it doesn't matter that it is under a lock
        // ReSharper disable once InconsistentlySynchronizedField - compiledRoutes is immutable
        public ISet<Route> Routes
        {
            get
            {
                ImmutableDictionary<string, CompiledRoute> snapshot = this.compiledRoutes;
                return new HashSet<Route>(snapshot.Values.Select(c => c.Route));
            }
        }

        public ISet<RouteResult> Evaluate(IMessage message)
        {
            // Because we are only reading here, it doesn't matter that it is under a lock
            // ReSharper disable once InconsistentlySynchronizedField - compiledRoutes is immutable
            ImmutableDictionary<string, CompiledRoute> snapshot = this.compiledRoutes;

            // Multiple routes for the same endpoint can exist, in which case
            // the message should be routed with the highest available priority
            var routes = snapshot.Values
                .Where(cr => cr.Route.Source.Match(message.MessageSource) && EvaluateInternal(cr, message))
                .Select(cr => new RouteResult(cr.Route.Endpoint, cr.Route.Priority, cr.Route.TimeToLiveSecs))
                .GroupBy(r => r.Endpoint)
                .Select(dupes => dupes.OrderBy(r => r.Priority).First());

            var results = new HashSet<RouteResult>(routes);

            // only use the fallback for telemetry messages
            if (!results.Any() && message.MessageSource.IsTelemetry())
            {
                // Handle fallback case
                this.fallback
                    .Filter(cr => EvaluateInternal(cr, message))
                    .ForEach(cr =>
                    {
                            results.Add(new RouteResult(cr.Route.Endpoint, cr.Route.Priority, cr.Route.TimeToLiveSecs));
                            Events.EvaluateFallback(cr.Route.Endpoint);
                    });
            }

            return results;
        }

        public void SetRoute(Route route)
        {
            lock (this.sync)
            {
                ImmutableDictionary<string, CompiledRoute> snapshot = this.compiledRoutes;
                this.compiledRoutes.Value = snapshot.SetItem(route.Id, this.Compile(route));
            }
        }

        public void RemoveRoute(string id)
        {
            lock (this.sync)
            {
                ImmutableDictionary<string, CompiledRoute> snapshot = this.compiledRoutes;
                this.compiledRoutes.Value = snapshot.Remove(id);
            }
        }

        public void ReplaceRoutes(ISet<Route> newRoutes)
        {
            lock (this.sync)
            {
                ImmutableDictionary<string, CompiledRoute> routesDict = Preconditions.CheckNotNull(newRoutes)
                    .ToImmutableDictionary(r => r.Id, r => this.Compile(r));
                this.compiledRoutes.Value = routesDict;
            }
        }

        public Task CloseAsync(CancellationToken token) => TaskEx.Done;

        static bool EvaluateInternal(CompiledRoute compiledRoute, IMessage message)
        {
            try
            {
                Bool evaluation = compiledRoute.Evaluate(message);

                if (evaluation.Equals(Bool.Undefined))
                {
                    Routing.UserAnalyticsLogger.LogUndefinedRouteEvaluation(message, compiledRoute.Route);
                }

                return evaluation;
            }
            catch (Exception ex)
            {
                Events.EvaluateFailure(compiledRoute.Route, ex);
                throw;
            }
        }

        CompiledRoute Compile(Route route)
        {
            Events.Compile(route);

            try
            {
                // Setting all flags for the compiler assuming this will only be invoked at runtime.
                Func<IMessage, Bool> evaluate = this.compiler.Compile(route, RouteCompilerFlags.All);
                var result = new CompiledRoute(route, evaluate);
                Events.CompileSuccess(route);
                return result;
            }
            catch (Exception ex)
            {
                Events.CompileFailure(route, ex);
                throw;
            }
        }

        class CompiledRoute
        {
            public CompiledRoute(Route route, Func<IMessage, Bool> evaluate)
            {
                this.Route = Preconditions.CheckNotNull(route);
                this.Evaluate = Preconditions.CheckNotNull(evaluate);
            }

            public Route Route { get; }

            public Func<IMessage, Bool> Evaluate { get; }
        }

        static class Events
        {
            const int IdStart = Routing.EventIds.Evaluator;
            static readonly ILogger Log = Routing.LoggerFactory.CreateLogger<Evaluator>();

            enum EventIds
            {
                Compile = IdStart,
                CompileSuccess,
                CompileFailure,
                EvaluatorFailure,
            }

            public static void Compile(Route route)
            {
                Log.LogInformation((int)EventIds.Compile, "[Compile] {0}", GetMessage("Compile began.", route));
            }

            public static void CompileSuccess(Route route)
            {
                Log.LogInformation((int)EventIds.CompileSuccess, "[CompileSuccess] {0}", GetMessage("Compile succeeded.", route));
            }

            public static void CompileFailure(Route route, Exception ex)
            {
                Log.LogError((int)EventIds.CompileFailure, ex, "[CompileFailure] {0}", GetMessage("Compile failed.", route));
            }

            public static void EvaluateFailure(Route route, Exception ex)
            {
                Log.LogError((int)EventIds.EvaluatorFailure, ex, "[EvaluateFailure] {0}", GetMessage("Evaluate failed.", route));
            }

            public static void EvaluateFallback(Endpoint endpoint)
            {
                Routing.UserMetricLogger.LogEgressFallbackMetric(1, endpoint.IotHubName);
            }

            static string GetMessage(string message, Route route)
            {
                return Invariant($"{message} RouteId: \"{route.Id}\" RouteCondition: \"{route.Condition}\"");
            }
        }
    }
}
