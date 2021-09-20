﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.UpgradeAssistant.Extensions
{
    [DebuggerDisplay("{Name}, {Location}")]
    public sealed record ExtensionInstance : IDisposable, IExtensionInstance
    {
        private const string ExtensionServiceProvidersSectionName = "ExtensionServiceProviders";
        public const string ManifestFileName = "ExtensionManifest.json";

        private const string ExtensionNamePropertyName = "ExtensionName";
        private const string DefaultExtensionName = "Unknown";

        private readonly Lazy<AssemblyLoadContext>? _alc;

        public ExtensionInstance(string instanceKey, IFileProvider fileProvider, string location, IConfiguration configuration, ILogger<ExtensionInstance> logger)
        {
            FileProvider = fileProvider;
            Location = location;
            Configuration = configuration;
            Name = GetName(Configuration, location);

            Version = GetVersion();
            var serviceProviders = GetOptions<string[]>(ExtensionServiceProvidersSectionName);

            if (serviceProviders is not null)
            {
                _alc = new Lazy<AssemblyLoadContext>(() => new ExtensionAssemblyLoadContext(instanceKey, this, serviceProviders, logger));
            }
        }

        /// <summary>
        /// Version needs to fit into a <see cref="Version"/> at the moment. Versions may have a '+' or '-' in it per semantic versioning so we'll just take the first part.
        /// </summary>
        private Version? GetVersion()
        {
            // Currently needs to fit a version, so we'll just skip any '-' or '+'
            var version = GetOptions<string>("Version");

            if (version is null)
            {
                return null;
            }

            var span = version.AsSpan();

            var index = span.IndexOf('-');

            if (index > 0)
            {
                span = span.Slice(0, index);
            }

            index = span.IndexOf('+');

            if (index > 0)
            {
                span = span.Slice(0, index);
            }

            return Version.Parse(span);
        }

        public string Name { get; }

        public void AddServices(IServiceCollection services)
        {
            if (_alc is null)
            {
                return;
            }

            var serviceProviders = _alc.Value.Assemblies
                .SelectMany(assembly => assembly.GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract && typeof(IExtensionServiceProvider).IsAssignableFrom(t))
                .Select(t => Activator.CreateInstance(t))
                .Cast<IExtensionServiceProvider>());

            foreach (var sp in serviceProviders)
            {
                sp.AddServices(new ExtensionServiceCollection(services, this));
            }
        }

        public string Location { get; }

        public IFileProvider FileProvider { get; }

        public IConfiguration Configuration { get; }

        public Version? Version { get; init; }

        public Version? MinUpgradeAssistantVersion => GetOptions<Version>("MinRequiredVersion");

        public string Description => GetOptions<string>("Description") ?? string.Empty;

        public IReadOnlyCollection<string> Authors => GetOptions<string[]>("Authors") ?? Array.Empty<string>();

        public T? GetOptions<T>(string sectionName) => Configuration.GetSection(sectionName).Get<T>();

        public void Dispose()
        {
            if (FileProvider is IDisposable disposable)
            {
                disposable.Dispose();

                if (_alc is not null && _alc.IsValueCreated)
                {
                    _alc.Value.Unload();
                }
            }
        }

        public bool IsInExtension(Assembly assembly)
        {
            if (_alc is not null && _alc.IsValueCreated)
            {
                return AssemblyLoadContext.GetLoadContext(assembly) == _alc.Value;
            }

            return false;
        }

        private static string GetName(IConfiguration configuration, string location)
        {
            if (configuration[ExtensionNamePropertyName] is string name)
            {
                return name;
            }

            if (Path.GetFileNameWithoutExtension(location) is string locationName)
            {
                return locationName;
            }

            return DefaultExtensionName;
        }
    }
}
