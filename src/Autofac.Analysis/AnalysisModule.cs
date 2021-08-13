﻿using System;
using System.Diagnostics;
using Autofac.Analysis.Display;
using Autofac.Analysis.Engine;
using Autofac.Analysis.Source;
using Autofac.Analysis.Transport.Connector;
using Autofac.Analysis.Transport.Messages;
using Autofac.Analysis.Transport.Model;
using Autofac.Core;
using Autofac.Core.Registration;
using Autofac.Core.Resolving;
using Serilog;

namespace Autofac.Analysis
{
    public class AnalysisModule : Module, IStartable, IDisposable
    {
        readonly ILifetimeScope _coreContainer, _sessionScope;
        readonly IWriteQueue _client;

        public AnalysisModule(ILogger outputLogger)
        {
            if (outputLogger == null)
                throw new ArgumentNullException(nameof(outputLogger));

            var client = new InProcQueue();
            var coreBuilder = new ContainerBuilder();
            coreBuilder.RegisterInstance(outputLogger).ExternallyOwned();
            coreBuilder.RegisterModule<CoreModule>();
            coreBuilder.RegisterModule(new DisplayModule(outputLogger));
            coreBuilder.RegisterInstance(client).As<IReadQueue>();
            _coreContainer = coreBuilder.Build();
            _sessionScope = _coreContainer.BeginLifetimeScope("profiler-session");
            _client = client;
            var session = _sessionScope.Resolve<IProfilerSession>();
            session.Start();
        }

        public void Dispose()
        {
            _sessionScope.Dispose();
            _coreContainer.Dispose();
        }

        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);
            builder.RegisterInstance(this)
                .As<IStartable>()
                .OwnedByLifetimeScope()
                .OnActivated(e => e.Instance.Start(e.Context.Resolve<ILifetimeScope>()));

            var processInfo = Process.GetCurrentProcess();
            Send(new ProfilerConnectedMessage(processInfo.MainModule.FileName, processInfo.Id));
        }


        protected override void AttachToComponentRegistration(IComponentRegistryBuilder componentRegistry, IComponentRegistration registration)
        { 
            base.AttachToComponentRegistration(componentRegistry, registration);
            
            var message = new ComponentAddedMessage(ModelMapper.GetComponentModel(registration));            
            Send(message);
        }

        protected override void AttachToRegistrationSource(IComponentRegistryBuilder componentRegistry, IRegistrationSource registrationSource)
        {
            base.AttachToRegistrationSource(componentRegistry, registrationSource);

            var message = new RegistrationSourceAddedMessage(ModelMapper.GetRegistrationSourceModel(registrationSource));
            Send(message);
        }

        public void Start() { }

        public void Start(ILifetimeScope rootLifetimeScope)
        {
            if (rootLifetimeScope == null) throw new ArgumentNullException(nameof(rootLifetimeScope));
            AttachToLifetimeScope(rootLifetimeScope);
        }

        void AttachToLifetimeScope(ILifetimeScope lifetimeScope, ILifetimeScope parent = null)
        {
            var lifetimeScopeModel = ModelMapper.GetLifetimeScopeModel(lifetimeScope, parent);
            var message = new LifetimeScopeBeginningMessage(lifetimeScopeModel);
            Send(message);

            lifetimeScope.CurrentScopeEnding += (s, e) =>
            {
                Send(new LifetimeScopeEndingMessage(lifetimeScopeModel.Id));
                ModelMapper.IdTracker.ForgetId(lifetimeScope);
            };

            lifetimeScope.ChildLifetimeScopeBeginning += (s, e) => AttachToLifetimeScope(e.LifetimeScope, lifetimeScope);
            lifetimeScope.ResolveOperationBeginning += (s, e) => AttachToResolveOperation(e.ResolveOperation, lifetimeScopeModel);
        }

        void AttachToResolveOperation(IResolveOperation resolveOperation, LifetimeScopeModel lifetimeScope)
        {
            var resolveOperationModel = ModelMapper.GetResolveOperationModel(resolveOperation, lifetimeScope, new StackTrace());
            Send(new ResolveOperationBeginningMessage(resolveOperationModel));
            resolveOperation.CurrentOperationEnding += (s, e) =>
            {
                var message = e.Exception != null ?
                    new ResolveOperationEndingMessage(resolveOperationModel.Id, e.Exception.GetType().AssemblyQualifiedName, e.Exception.Message) :
                    new ResolveOperationEndingMessage(resolveOperationModel.Id);
                Send(message);
            };
            resolveOperation.InstanceLookupBeginning += (s, e) => AttachToInstanceLookup(e.InstanceLookup, resolveOperationModel);
        }

        void AttachToInstanceLookup(IInstanceLookup instanceLookup, ResolveOperationModel resolveOperation)
        {
            var instanceLookupModel = ModelMapper.GetInstanceLookupModel(instanceLookup, resolveOperation);
            Send(new InstanceLookupBeginningMessage(instanceLookupModel));
            instanceLookup.InstanceLookupEnding += (s, e) => Send(new InstanceLookupEndingMessage(instanceLookupModel.Id, e.NewInstanceActivated));
            instanceLookup.CompletionBeginning += (s, e) => Send(new InstanceLookupCompletionBeginningMessage(instanceLookupModel.Id));
            instanceLookup.CompletionEnding += (s, e) => Send(new InstanceLookupCompletionEndingMessage(instanceLookupModel.Id));
        }

        void Send(object message)
        {
            _client.Enqueue(message);
        }

        internal ModelMapper ModelMapper { get; } = new ModelMapper();
    }
}
