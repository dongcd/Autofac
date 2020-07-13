﻿// This software is part of the Autofac IoC container
// Copyright © 2020 Autofac Contributors
// https://autofac.org
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using Autofac.Core.Resolving;

namespace Autofac.Core.Diagnostics
{
    /// <summary>
    /// Provides a resolve pipeline tracer that generates DOT graph output
    /// traces for an end-to-end operation flow. Attach to the
    /// <see cref="FullOperationDiagnosticTracerBase.OperationCompleted"/>
    /// event to receive notifications when a new graph is available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tracer subscribes to all Autofac diagnostic events and can't be
    /// unsubscribed. This is required to ensure beginning and end of each
    /// logical activity can be captured.
    /// </para>
    /// </remarks>
    public class DotDiagnosticTracer : FullOperationDiagnosticTracerBase
    {
        /// <summary>
        /// Metadata flag to help deduplicate the number of places where the exception is traced.
        /// </summary>
        private const string RequestExceptionTraced = "__RequestException";

        private readonly ConcurrentDictionary<ResolveOperationBase, DotGraphBuilder> _operationBuilders = new ConcurrentDictionary<ResolveOperationBase, DotGraphBuilder>();

        /// <summary>
        /// Initializes a new instance of the <see cref="DotDiagnosticTracer"/> class.
        /// </summary>
        public DotDiagnosticTracer()
            : base()
        {
        }

        /// <summary>
        /// Gets the number of operations in progress being traced.
        /// </summary>
        /// <value>
        /// An <see cref="int"/> with the number of trace IDs associated
        /// with in-progress operations being traced by this tracer.
        /// </value>
        public override int OperationsInProgress => _operationBuilders.Count;

        /// <inheritdoc/>
        public override void OnOperationStart(OperationStartDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            var builder = _operationBuilders.GetOrAdd(data.Operation, k => new DotGraphBuilder());
        }

        /// <inheritdoc/>
        public override void OnRequestStart(RequestDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.Operation, out var builder))
            {
                builder.OnRequestStart(
                    data.RequestContext.Service.ToString(),
                    data.RequestContext.Registration.Activator.DisplayName(),
                    data.RequestContext.DecoratorTarget?.Activator.DisplayName());
            }
        }

        /// <inheritdoc/>
        public override void OnMiddlewareStart(MiddlewareDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.RequestContext.Operation, out var builder))
            {
                builder.OnMiddlewareStart(data.Middleware.ToString());
            }
        }

        /// <inheritdoc/>
        public override void OnMiddlewareFailure(MiddlewareDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.RequestContext.Operation, out var builder))
            {
                builder.OnMiddlewareFailure();
            }
        }

        /// <inheritdoc/>
        public override void OnMiddlewareSuccess(MiddlewareDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.RequestContext.Operation, out var builder))
            {
                builder.OnMiddlewareSuccess();
            }
        }

        /// <inheritdoc/>
        public override void OnRequestFailure(RequestFailureDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.Operation, out var builder))
            {
                var requestException = data.RequestException;
                if (requestException is DependencyResolutionException && requestException.InnerException is object)
                {
                    requestException = requestException.InnerException;
                }

                if (requestException.Data.Contains(RequestExceptionTraced))
                {
                    builder.OnRequestFailure(null);
                }
                else
                {
                    builder.OnRequestFailure(requestException);
                }

                requestException.Data[RequestExceptionTraced] = true;
            }
        }

        /// <inheritdoc/>
        public override void OnRequestSuccess(RequestDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.Operation, out var builder))
            {
                builder.OnRequestSuccess(data.RequestContext.Instance?.GetType().ToString());
            }
        }

        /// <inheritdoc/>
        public override void OnOperationFailure(OperationFailureDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.Operation, out var builder))
            {
                try
                {
                    builder.OnOperationFailure(data.OperationException);

                    OnOperationCompleted(new OperationTraceCompletedArgs(data.Operation, builder.ToString()));
                }
                finally
                {
                    _operationBuilders.TryRemove(data.Operation, out var _);
                }
            }
        }

        /// <inheritdoc/>
        public override void OnOperationSuccess(OperationSuccessDiagnosticData data)
        {
            if (data is null)
            {
                return;
            }

            if (_operationBuilders.TryGetValue(data.Operation, out var builder))
            {
                try
                {
                    builder.OnOperationSuccess(data.ResolvedInstance?.GetType().ToString());

                    OnOperationCompleted(new OperationTraceCompletedArgs(data.Operation, builder.ToString()));
                }
                finally
                {
                    _operationBuilders.TryRemove(data.Operation, out var _);
                }
            }
        }

        private abstract class DotGraphNode
        {
            public string Id { get; } = "n" + Guid.NewGuid().ToString("N");

            public bool Success { get; set; }
        }

        private class ResolveRequestNode : DotGraphNode
        {
            public ResolveRequestNode(string service, string component)
            {
                Service = service;
                Component = component;
                Middleware = new List<MiddlewareNode>();
            }

            public string Service { get; private set; }

            public string Component { get; private set; }

            public string? DecoratorTarget { get; set; }

            public Exception? Exception { get; set; }

            public string? InstanceType { get; set; }

            public List<MiddlewareNode> Middleware { get; }

            public void ToString(StringBuilder stringBuilder)
            {
                stringBuilder.StartTableNode(Id, border: 1)
                    .AppendTableRow(TracerMessages.ServiceDisplay, Service)
                    .AppendTableRow(TracerMessages.ComponentDisplay, Component);

                if (DecoratorTarget is object)
                {
                    stringBuilder.AppendTableRow(TracerMessages.TargetDisplay, DecoratorTarget);
                }

                if (InstanceType is object)
                {
                    stringBuilder.AppendTableRow(TracerMessages.InstanceDisplay, InstanceType);
                }

                if (Exception is object)
                {
                    stringBuilder.AppendTableRow(TracerMessages.ExceptionDisplay, Exception.ToString());
                }

                stringBuilder.EndTableNode();

                if (Middleware.Count != 0)
                {
                    var middlewareSubgraphId = "mw" + Id;
                    stringBuilder
                        .ConnectNodes(Id, middlewareSubgraphId, Middleware.Any(mw => !mw.Success))
                        .StartTableNode(middlewareSubgraphId, cellBorder: 1);

                    foreach (var mw in Middleware)
                    {
                        stringBuilder.AppendTableRow(!mw.Success, mw.Id, "{0}", mw.Name);
                    }

                    stringBuilder.EndTableNode();
                    foreach (var mw in Middleware)
                    {
                        mw.ToString(stringBuilder, middlewareSubgraphId);
                    }
                }
            }
        }

        private class OperationNode : DotGraphNode
        {
            public Exception? Exception { get; set; }

            public string? InstanceType { get; set; }

            public List<ResolveRequestNode> ResolveRequests { get; } = new List<ResolveRequestNode>();

            public void ToString(StringBuilder stringBuilder)
            {
                stringBuilder.Append(Id);
                stringBuilder.Append(" [label=\"");

                if (InstanceType is object)
                {
                    stringBuilder.Append(InstanceType);
                }
                else if (Exception is object)
                {
                    stringBuilder.Append(HttpUtility.HtmlEncode(Exception.ToString()).NewlineReplace("\\l"));
                    stringBuilder.Append("\\l");
                }

                stringBuilder.Append("\"]");
                stringBuilder.AppendLine();
                foreach (var request in ResolveRequests)
                {
                    request.ToString(stringBuilder);
                    stringBuilder.ConnectNodes(Id, request.Id, !request.Success);
                }
            }
        }

        private class MiddlewareNode : DotGraphNode
        {
            public MiddlewareNode(string name)
            {
                Name = name;
                ResolveRequests = new List<ResolveRequestNode>();
            }

            public string Name { get; }

            public List<ResolveRequestNode> ResolveRequests { get; }

            public void ToString(StringBuilder stringBuilder, string middlewareSubgraphId)
            {
                var fullId = string.Format(CultureInfo.CurrentCulture, "{0}:{1}", middlewareSubgraphId, Id);
                foreach (var request in ResolveRequests)
                {
                    stringBuilder.ConnectNodes(fullId, request.Id, !request.Success);
                    request.ToString(stringBuilder);
                }
            }
        }

        /// <summary>
        /// Generator for DOT format graph traces.
        /// </summary>
        private class DotGraphBuilder
        {
            public OperationNode Root { get; private set; }

            public Stack<ResolveRequestNode> ResolveRequests { get; } = new Stack<ResolveRequestNode>();

            public Stack<MiddlewareNode> Middlewares { get; } = new Stack<MiddlewareNode>();

            public DotGraphBuilder()
            {
                Root = new OperationNode();
            }

            public void OnOperationFailure(Exception? operationException)
            {
                Root.Success = false;
                Root.Exception = operationException;
            }

            public void OnOperationSuccess(string? instanceType)
            {
                Root.Success = true;
                Root.InstanceType = instanceType;
            }

            public void OnRequestStart(string service, string component, string? decoratorTarget)
            {
                var request = new ResolveRequestNode(service, component);
                if (decoratorTarget is object)
                {
                    request.DecoratorTarget = decoratorTarget;
                }

                if (Middlewares.Count != 0)
                {
                    Middlewares.Peek().ResolveRequests.Add(request);
                }
                else
                {
                    Root.ResolveRequests.Add(request);
                }

                ResolveRequests.Push(request);
            }

            public void OnRequestFailure(Exception? requestException)
            {
                if (ResolveRequests.Count == 0)
                {
                    // OnRequestFailure happened without a corresponding OnRequestStart.
                    return;
                }

                var request = ResolveRequests.Pop();
                request.Success = false;
                request.Exception = requestException;
            }

            public void OnRequestSuccess(string? instanceType)
            {
                if (ResolveRequests.Count == 0)
                {
                    // OnRequestSuccess happened without a corresponding OnRequestStart.
                    return;
                }

                var request = ResolveRequests.Pop();
                request.Success = true;
                request.InstanceType = instanceType;
            }

            public void OnMiddlewareStart(string middleware)
            {
                if (ResolveRequests.Count == 0)
                {
                    // Middleware only happens in context of a request.
                    return;
                }

                var mw = new MiddlewareNode(middleware);
                ResolveRequests.Peek().Middleware.Add(mw);
                Middlewares.Push(mw);
            }

            public void OnMiddlewareFailure()
            {
                if (Middlewares.Count == 0)
                {
                    // We somehow missed the start event.
                    return;
                }

                var mw = Middlewares.Pop();
                mw.Success = false;
            }

            public void OnMiddlewareSuccess()
            {
                if (Middlewares.Count == 0)
                {
                    // We somehow missed the start event.
                    return;
                }

                var mw = Middlewares.Pop();
                mw.Success = true;
            }

            public override string ToString()
            {
                var builder = new StringBuilder();
                builder.AppendLine("digraph G {");
                Root.ToString(builder);
                builder.AppendLine("}");
                return builder.ToString();
            }
        }
    }
}
