using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Orleans.Providers
{
    //supposed to be use as a singleton in runtime to keep track of all loaded ProviderTypeLoader
    internal class LoadedProviderTypeLoaders
    {
        internal ConcurrentBag<ProviderTypeLoader> Managers { get; private set; }
        private readonly Logger logger;
        public LoadedProviderTypeLoaders()
        {
            this.Managers = new ConcurrentBag<ProviderTypeLoader>();
            AppDomain.CurrentDomain.AssemblyLoad += ProcessNewAssembly;
            this.logger = LogManager.GetLogger("ProviderTypeLoader", LoggerType.Runtime);
        }

        private void ProcessNewAssembly(object sender, AssemblyLoadEventArgs args)
        {
#if !NETSTANDARD
            // If the assembly is loaded for reflection only avoid processing it.
            if (args.LoadedAssembly.ReflectionOnly)
            {
                return;
            }
#endif

            // We do this under the lock to avoid race conditions when an assembly is added 
            // while a type manager is initializing.
            lock (this.Managers)
            {
                // We assume that it's better to fetch and iterate through the list of types once,
                // and the list of TypeManagers many times, rather than the other way around.
                // Certainly it can't be *less* efficient to do it this way.
                foreach (var type in TypeUtils.GetDefinedTypes(args.LoadedAssembly, logger))
                {
                    foreach (var mgr in Managers)
                    {
                        if (mgr.IsActive)
                        {
                            mgr.ProcessType(type);
                        }
                    }
                }
            }
        }
    }

    internal class ProviderTypeLoader
    {
        private readonly Func<Type, bool> condition;
        private readonly Action<Type> callback;
        private readonly HashSet<Type> alreadyProcessed;
        public bool IsActive { get; set; }

        private readonly Logger logger = LogManager.GetLogger("ProviderTypeLoader", LoggerType.Runtime);


        public ProviderTypeLoader(Func<Type, bool> condition, Action<Type> action)
        {
            this.condition = condition;
            callback = action;
            alreadyProcessed = new HashSet<Type>();
            IsActive = true;
         }

        public static void AddProviderTypeManager(Func<Type, bool> condition, Action<Type> action, LoadedProviderTypeLoaders loadedProviderTypeLoadersSingleton)
        {
            var manager = new ProviderTypeLoader(condition, action);
            lock (loadedProviderTypeLoadersSingleton.Managers)
            {
                loadedProviderTypeLoadersSingleton.Managers.Add(manager);
            }

            manager.ProcessLoadedAssemblies(loadedProviderTypeLoadersSingleton);
        }

        private void ProcessLoadedAssemblies(LoadedProviderTypeLoaders loadedProviderTypeLoadersSingleton)
        {
            lock (loadedProviderTypeLoadersSingleton.Managers)
            {
                // Walk through already-loaded assemblies. 
                // We do this under the lock to avoid race conditions when an assembly is added 
                // while a type manager is initializing.
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    ProcessAssemblyLocally(assembly);
                }
            }
        }

        internal void ProcessType(TypeInfo typeInfo)
        {
            var type = typeInfo.AsType();
            if (alreadyProcessed.Contains(type) || typeInfo.IsInterface || typeInfo.IsAbstract || !condition(type)) return;

            alreadyProcessed.Add(type);
            callback(type);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ProcessAssemblyLocally(Assembly assembly)
        {
            if (!IsActive) return;

            try
            {
                foreach (var type in TypeUtils.GetDefinedTypes(assembly, logger))
                {
                    ProcessType(type);
                }
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.Provider_AssemblyLoadError,
                    "Error searching for providers in assembly {0} -- ignoring this assembly. Error = {1}", assembly.FullName, exc);
            }
        }
    }
}
