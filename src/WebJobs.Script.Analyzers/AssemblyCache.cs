// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Azure.Functions.Analyzers
{
    // Only support extensions and WebJobs core.
    // Although extensions may refer to other dlls.
    public class AssemblyCache
    {
        // Map from assembly identities to full paths
        public static AssemblyCache Instance = new AssemblyCache();

        bool _registered;

        // Assembly Display Name --> Path
        Dictionary<string, string> _map = new Dictionary<string, string>();

        // Assembly Display Name --> loaded Assembly object
        Dictionary<string, Assembly> _mapRef = new Dictionary<string, Assembly>();

        JobHostMetadataProvider _jobHostMetadataProvider;

        internal JobHostMetadataProvider JobHostMetadataProvider => _jobHostMetadataProvider;
        private int _projectCount;

        // $$$ This can get invoked multiple times concurrently
        // This will get called on every compilation.
        // So return early on subsequent initializations.
        internal void Build(Compilation compilation)
        {
            Register();

            int count;
            lock (this)
            {
                // If project references have changed, then reanalyze to pick up new dependencies.
                var refs = compilation.References.OfType<PortableExecutableReference>().ToArray();
                count = refs.Length;
                if ((count == _projectCount) && (_jobHostMetadataProvider != null))
                {
                    return; // already initialized.
                }

                // Even for netStandard/.core projects, this will still be a flattened list of the full transitive closure of dependencies.
                foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
                {
                    var dispName = reference.Display; // For .net core, the displayname can be the full path
                    var path = reference.FilePath;

                    _map[dispName] = path;
                }

                // Builtins
                _mapRef["mscorlib"] = typeof(object).Assembly;
                _mapRef[Constants.Assemblies.WebJobsAssemblyName] = typeof(Microsoft.Azure.WebJobs.FunctionNameAttribute).Assembly;
                _mapRef[Constants.Assemblies.WebJobsHostAssemblyName] = typeof(Microsoft.Azure.WebJobs.JobHost).Assembly;

                // JSON.Net?
            }

            // Produce tooling object
            var webjobsStartups = new List<Type>();
            foreach (var path in _map.Values)
            {
                // We don't want to load and reflect over every dll.
                // By convention, restrict based on filenames.
                var filename = Path.GetFileName(path);
                // TODO: I assume this is so general to avoid missing custom extensions, but can it be tightened up for performance?
                if (!filename.ToLowerInvariant().Contains("extension"))
                {
                    continue;
                }
                if (path.Contains(@"\ref\"))    // Skip reference assemblies.
                {
                    continue;
                }

                Assembly loadedAssembly;
                try
                {
                    // See GetNuGetPackagesPath for details
                    // Script runtime is already setup with assembly resolution hooks, so use LoadFrom
                    loadedAssembly = Assembly.LoadFrom(path);

                    string assemblyName = new AssemblyName(loadedAssembly.FullName).Name;
                    _mapRef[assemblyName] = loadedAssembly;

                    var startupAttr = loadedAssembly.GetCustomAttributes<WebJobsStartupAttribute>().Select(a => a.WebJobsStartupType);
                    if (startupAttr.Count() > 0)
                    {
                        webjobsStartups.AddRange(startupAttr);
                    }
                }
                catch (Exception e)
                {
                    // Could be a reference assembly.
                    continue;
                }
            }

            var host = new HostBuilder()
                .ConfigureWebJobs(b =>
                {
                    b.AddAzureStorageCoreServices()
                        .UseExternalStartup(new CompilationWebJobsStartupTypeLocator(_mapRef.Values.ToArray()));
                })
                .Build();
            var tooling = (JobHostMetadataProvider)host.Services.GetRequiredService<IJobHostMetadataProvider>();

            lock (this)
            {
                this._projectCount = count;
                this._jobHostMetadataProvider = tooling;
            }
        }

        public void Register()
        {
            if (_registered)
            {
                return;
            }
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            _registered = true;
        }

        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name);

            Assembly mappedAssembly;
            if (_mapRef.TryGetValue(assemblyName.Name, out mappedAssembly))
            {
                return mappedAssembly;
            }

            mappedAssembly = LoadFromProjectReference(assemblyName);
            if (mappedAssembly != null)
            {
                _mapRef[assemblyName.Name] = mappedAssembly;
            }

            return mappedAssembly;
        }

        private Assembly LoadFromProjectReference(AssemblyName assemblyName)
        {
            foreach (var kv in _map)
            {
                var path = kv.Key;
                if (path.Contains(@"\ref\")) // Skip reference assemblies.
                {
                    continue;
                }

                var filename = Path.GetFileNameWithoutExtension(path);

                // Simplifying assumption: assume dll name matches assembly name.
                // Use this as a filter to limit the number of file-touches.
                if (string.Equals(filename, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var assemblyNameFromPath = AssemblyName.GetAssemblyName(path);

                    if (string.Equals(assemblyNameFromPath.FullName, assemblyName.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        var loadedAssembly = Assembly.LoadFrom(path);
                        return loadedAssembly;
                    }
                }
            }
            return null;
        }
    }
}
