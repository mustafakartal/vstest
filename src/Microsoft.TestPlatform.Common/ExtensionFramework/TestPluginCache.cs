// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Common.ExtensionFramework
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    
    using Microsoft.VisualStudio.TestPlatform.Common.ExtensionFramework.Utilities;
    using Microsoft.VisualStudio.TestPlatform.Common.Utilities;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.Utilities.Helpers;
    using Microsoft.VisualStudio.TestPlatform.Utilities.Helpers.Interfaces;
#if NET46
    using System.Threading;
#else
    using System.Runtime.Loader;
#endif

    /// <summary>
    /// The test plugin cache.
    /// </summary>
    /// <remarks>Making this a singleton to offer better unit testing.</remarks>
    public class TestPluginCache
    {
        #region Private Members

        private readonly Dictionary<string, Assembly> resolvedAssemblies;
        private readonly IFileHelper fileHelper;

        /// <summary>
        /// Specify the path to additional extensions
        /// </summary>
        private IEnumerable<string> pathToAdditionalExtensions;
        
        /// <summary>
        /// Specifies whether we should load only well known extensions or not. Default is "load all".
        /// </summary>
        private bool loadOnlyWellKnownExtensions;

        /// <summary>
        /// Assembly resolver used to resolve the additional extensions
        /// </summary>
        private AssemblyResolver assemblyResolver;

        /// <summary>
        /// Lock for additional extensions update
        /// </summary>
        private object lockForAdditionalExtensionsUpdate;

        private static TestPluginCache instance;

        private List<string> defaultExtensionPaths;

        private const string DefaultExtensionsFolder = "Extensions";

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="TestPluginCache"/> class. 
        /// </summary>
        protected TestPluginCache(IFileHelper fileHelper)
        {
            this.resolvedAssemblies = new Dictionary<string, Assembly>();
            this.pathToAdditionalExtensions = null;
            this.loadOnlyWellKnownExtensions = false;
            this.lockForAdditionalExtensionsUpdate = new object();
            this.fileHelper = fileHelper;
        }

        #endregion

        #region Public Properties

        public static TestPluginCache Instance
        {
            get
            {
                return instance ?? (instance = new TestPluginCache(new FileHelper()));
            }

            internal set
            {
                instance = value;
            }
        }


        /// <summary>
        /// Gets whether default extensions discovery is done.
        /// </summary>
        public bool AreDefaultExtensionsDiscovered
        {
            get;
            private set;
        }

        /// <summary>
        /// The test extensions discovered by the cache until now.
        /// </summary>
        /// <remarks>Returns null if discovery of extensions is not done.</remarks>
        internal TestExtensions TestExtensions { get; private set; }

        /// <summary>
        /// Path to additional extensions
        /// </summary>
        public IEnumerable<string> PathToAdditionalExtensions
        {
            get
            {
                return this.pathToAdditionalExtensions;
            }
        }

        /// <summary>
        /// Specific whether only well known extensions should be loaded or not
        /// </summary>
        public bool LoadOnlyWellKnownExtensions
        {
            get
            {
                return this.loadOnlyWellKnownExtensions;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the test extensions defined in the provided extension assembly.
        /// </summary>
        /// <param name="extensionAssembly">The extension assembly.</param>
        /// <returns>The test extension collection defined in this assembly.</returns>
        public TestExtensions GetTestExtensions(string extensionAssembly)
        {
            // Check if extensions from this assembly have already been discovered.
            var extensions = this.TestExtensions?.GetExtensionsDiscoveredFromAssembly(extensionAssembly);

            if (extensions != null)
            {
                return extensions;
            }

            this.SetupAssemblyResolver(extensionAssembly);

            try
            {
                EqtTrace.Verbose("TestPluginCache: Discovering the extensions using extension path.");

                extensions = this.GetTestExtensions(new List<string> { extensionAssembly });
                
                // Add extensions discovered to the cache.
                if (this.TestExtensions == null)
                {
                    this.TestExtensions = extensions;
                }
                else
                {
                    this.TestExtensions.AddExtensions(extensions);
                }

                if (EqtTrace.IsVerboseEnabled)
                {
                    EqtTrace.Verbose(
                        "TestPluginCache: Discovered extensions from '{0}'.",
                        extensionAssembly);
                }

                this.LogExtensions();
            }
#if NET46
            catch (ThreadAbortException)
            {
                // Nothing to do here, we just do not want to do an EqtTrace.Fail for this thread
                // being aborted as it is a legitimate exception to receive.
                if (EqtTrace.IsVerboseEnabled)
                {
                    EqtTrace.Verbose("TestPluginCache.DiscoverTestExtensions: Data extension discovery is being aborted due to a thread abort.");
                }
            }
#endif
            catch (Exception e)
            {
                EqtTrace.Error("TestPluginCache: Discovery of extension from {0} failed! {1}", extensionAssembly, e);
                throw;
            }

            return extensions;
        }

        /// <summary>
        /// Performs discovery of data extensions.
        /// </summary>
        [System.Security.SecurityCritical]
        public void DiscoverAllTestExtensions()
        {
            this.SetupAssemblyResolver(null);
            
            // Some times TestPlatform.core.dll assembly fails to load in the current appdomain (from devenv.exe).
            // Reason for failures are not known. Below handler, again calls assembly.load() in failing assembly
            // and that succeeds.
            // Because of this assembly failure, below domain.CreateInstanceAndUnwrap() call fails with error
            // "Unable to cast transparent proxy to type 'Microsoft.VisualStudio.TestPlatform.Core.TestPluginsFramework.TestPluginDiscoverer"
#if NET46
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomainAssemblyResolve);
#else
            AssemblyLoadContext.Default.Resolving += this.CurrentDomainAssemblyResolve;
#endif
            try
            {
                EqtTrace.Verbose("TestPluginCache: Discovering the extensions using extension path.");
                
                // Combine all the possible extensions - both default and additional
                var allExtensionPaths = new List<string>(this.DefaultExtensionPaths);
                if (this.pathToAdditionalExtensions != null)
                {
                    allExtensionPaths.AddRange(this.pathToAdditionalExtensions);
                }

                // Discover the test extensions from candidate assemblies.
                var extensions = this.GetTestExtensions(allExtensionPaths);

                if (this.TestExtensions == null)
                {
                    this.TestExtensions = extensions;
                }
                else
                {
                    this.TestExtensions.AddExtensions(extensions);
                }

                this.AreDefaultExtensionsDiscovered = true;

                if (EqtTrace.IsVerboseEnabled)
                {
                    var extensionString = this.pathToAdditionalExtensions != null
                                              ? string.Join(",", this.pathToAdditionalExtensions.ToArray())
                                              : null;
                    EqtTrace.Verbose(
                        "TestPluginCache: Discovered the extensions using extension path '{0}'.",
                        extensionString);
                }

                this.LogExtensions();
            }
#if NET46
                catch (ThreadAbortException)
                {
                    // Nothing to do here, we just do not want to do an EqtTrace.Fail for this thread
                    // being aborted as it is a legitimate exception to receive.
                    if (EqtTrace.IsVerboseEnabled)
                    {
                        EqtTrace.Verbose("TestPluginCache.DiscoverTestExtensions: Data extension discovery is being aborted due to a thread abort.");
                    }
                }
#endif
            catch (Exception e)
            {
                EqtTrace.Error("TestPluginCache: Discovery failed! {0}", e);
                throw;
            }
            finally
            {
#if NET46
                AppDomain.CurrentDomain.AssemblyResolve -= new ResolveEventHandler(CurrentDomainAssemblyResolve);
#else
                AssemblyLoadContext.Default.Resolving -= this.CurrentDomainAssemblyResolve;
#endif

                // clear the assemblies
                lock (this.resolvedAssemblies)
                {
                    this.resolvedAssemblies?.Clear();
                }
            }
        }

        /// <summary>
        /// Use the parameter path to extensions
        /// </summary>
        public void UpdateAdditionalExtensions(IEnumerable<string> additionalExtensionsPath, bool shouldLoadOnlyWellKnownExtensions)
        {
            lock (this.lockForAdditionalExtensionsUpdate)
            {
                EqtTrace.Verbose(
                    "TestPluginCache: Updating loadOnlyWellKnownExtensions from {0} to {1}.",
                    this.loadOnlyWellKnownExtensions,
                    shouldLoadOnlyWellKnownExtensions);

                this.loadOnlyWellKnownExtensions = shouldLoadOnlyWellKnownExtensions;

                IList<string> extensions = additionalExtensionsPath?.ToList();
                if (extensions == null || extensions.Count == 0)
                {
                    return;
                }

                string extensionString;
                if (this.pathToAdditionalExtensions != null
                    && extensions.Count == this.pathToAdditionalExtensions.Count()
                    && extensions.All(e => this.pathToAdditionalExtensions.Contains(e)))
                {
                    extensionString = this.pathToAdditionalExtensions != null
                                          ? string.Join(",", this.pathToAdditionalExtensions.ToArray())
                                          : null;
                    EqtTrace.Verbose(
                        "TestPluginCache: Ignoring the new extensions update as there is no change. Current extensions are '{0}'.",
                        extensionString);

                    return;
                }

                // Don't do a strict check for existence of the extension path. The extension paths may or may
                // not exist on the disk. In case of .net core, the paths are relative to the nuget packages
                // directory. The path to nuget directory is automatically setup for CLR to resolve.
                // Test platform tries to load every extension by assembly name. If it is not resolved, we don't
                // an error.
                var newExtensionPaths = extensions.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (this.pathToAdditionalExtensions != null)
                {
                    newExtensionPaths.AddRange(this.pathToAdditionalExtensions);
                }

                // Use the new paths and set the extensions discovered to false so that the next time 
                // any one tries to get the additional extensions, we rediscover. 
                this.pathToAdditionalExtensions = newExtensionPaths;

                this.AreDefaultExtensionsDiscovered = false;

                if (EqtTrace.IsVerboseEnabled)
                {
                    var directories =
                        this.pathToAdditionalExtensions.Select(e => Path.GetDirectoryName(Path.GetFullPath(e))).Distinct();

                    var directoryString = directories != null ? string.Join(",", directories.ToArray()) : null;
                    EqtTrace.Verbose(
                        "TestPluginCache: Using directories for assembly resolution '{0}'.",
                        directoryString);

                    extensionString = this.pathToAdditionalExtensions != null
                                          ? string.Join(",", this.pathToAdditionalExtensions.ToArray())
                                          : null;
                    EqtTrace.Verbose("TestPluginCache: Updated the available extensions to '{0}'.", extensionString);
                }
            }
        }

        #endregion

        #region Utility methods

        internal IEnumerable<string> DefaultExtensionPaths
        {
            get
            {
                if (this.defaultExtensionPaths == null)
                {
                    var extensionsFolder = Path.Combine(Path.GetDirectoryName(typeof(TestPluginCache).GetTypeInfo().Assembly.Location), DefaultExtensionsFolder);

                    if (!this.DoesDirectoryExist(extensionsFolder))
                    {
                        EqtTrace.Error("Default extensions folder does not exist");
                        // Initialize and bail out.
                        this.defaultExtensionPaths = new List<string>();
                    }
                    else
                    {
                        // Scan all of the DLL's and EXE's
                        var dlls = this.GetFilesInDirectory(extensionsFolder, ".*.dll");
                        this.defaultExtensionPaths = new List<string>(dlls);
                        var exes = this.GetFilesInDirectory(extensionsFolder, ".*.exe");
                        this.defaultExtensionPaths.AddRange(exes);
                    }
                }

                return this.defaultExtensionPaths;
            }
        }

        /// <summary>
        ///  Checks if a directory exists
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        /// <remarks>Added to mock out FileSystem interaction for unit testing.</remarks>
        internal virtual bool DoesDirectoryExist(string path)
        {
            return this.fileHelper.DirectoryExists(path);
        }

        /// <summary>
        /// Gets files in a directory.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="searchPattern"></param>
        /// <returns></returns>
        /// <remarks>Added to mock out FileSystem interaction for unit testing.</remarks>
        internal virtual string[] GetFilesInDirectory(string path, string searchPattern)
        {
            return this.fileHelper.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly).ToArray();
        }

        /// <summary>
        /// Gets the test extensions defined in the extension assembly list.
        /// </summary>
        /// <param name="extensionPaths">Extension assembly paths.</param>
        /// <returns>List of extensions.</returns>
        /// <remarks>Added to mock out dependency from the actual test plugin discovery as such.</remarks>
        internal virtual TestExtensions GetTestExtensions(IEnumerable<string> extensionPaths)
        {
            var discoverer = new TestPluginDiscoverer();

            return discoverer.GetTestExtensionsInformation(extensionPaths, this.loadOnlyWellKnownExtensions);
        }

        /// <summary>
        /// Gets the resolution paths for the extension assembly to facilitate assembly resolution.
        /// </summary>
        /// <param name="extensionAssembly">The extension assembly.</param>
        /// <returns>Resolution paths for the assembly.</returns>
        internal IList<string> GetResolutionPaths(string extensionAssembly)
        {
            var resolutionPaths = new List<string>();

            var extensionDirectory = Path.GetDirectoryName(Path.GetFullPath(extensionAssembly));
            resolutionPaths.Add(extensionDirectory);

            var currentDirectory = Path.GetDirectoryName(typeof(TestPluginCache).GetTypeInfo().Assembly.Location);
            if (!resolutionPaths.Contains(currentDirectory))
            {
                resolutionPaths.Add(currentDirectory);
            }

            return resolutionPaths;
        }

        /// <summary>
        /// Gets the default set of resolution paths for the assembly resolution
        /// </summary>
        /// <returns>List of paths.</returns>
        [System.Security.SecurityCritical]
        internal IList<string> GetDefaultResolutionPaths()
        {
            var resolutionPaths = new List<string>();
            
            var extensionDirectories = this.pathToAdditionalExtensions?.Select(e => Path.GetDirectoryName(Path.GetFullPath(e))).Distinct();
            if (extensionDirectories != null && extensionDirectories.Any())
            {
                resolutionPaths.AddRange(extensionDirectories);
            }

            var currentDirectory = Path.GetDirectoryName(typeof(TestPluginCache).GetTypeInfo().Assembly.Location);
            
            if (!resolutionPaths.Contains(currentDirectory))
            {
                resolutionPaths.Add(currentDirectory);
            }

            var defaultExtensionsFolder = Path.Combine(Path.GetDirectoryName(typeof(TestPluginCache).GetTypeInfo().Assembly.Location), DefaultExtensionsFolder);

            if (this.DoesDirectoryExist(defaultExtensionsFolder) && !resolutionPaths.Contains(defaultExtensionsFolder))
            {
                resolutionPaths.Add(defaultExtensionsFolder);
            }

            return resolutionPaths;
        }
        
        private void SetupAssemblyResolver(string extensionAssembly)
        {
            IList<string> resolutionPaths;

            if (string.IsNullOrEmpty(extensionAssembly))
            {
                resolutionPaths = this.GetDefaultResolutionPaths();
            }
            else
            {
                resolutionPaths = this.GetResolutionPaths(extensionAssembly);
            }

            // Add assembly resolver which can resolve the extensions from the specified directory.
            if (this.assemblyResolver == null)
            {
                this.assemblyResolver = new AssemblyResolver(resolutionPaths);
            }
            else
            {
                this.assemblyResolver.AddSearchDirectories(resolutionPaths);
            }
        }

#if NET46
        private Assembly CurrentDomainAssemblyResolve(object sender, ResolveEventArgs args)
#else
        private Assembly CurrentDomainAssemblyResolve(AssemblyLoadContext loadContext, AssemblyName args)
#endif
        {
            var assemblyName = new AssemblyName(args.Name);

            Assembly assembly = null;
            lock (this.resolvedAssemblies)
            {
                try
                {
                    EqtTrace.Verbose("CurrentDomain_AssemblyResolve: Resolving assembly '{0}'.", args.Name);

                    if (this.resolvedAssemblies.TryGetValue(args.Name, out assembly))
                    {
                        return assembly;
                    }

                    // Put it in the resolved assembly so that if below Assembly.Load call
                    // triggers another assembly resolution, then we dont end up in stack overflow
                    this.resolvedAssemblies[args.Name] = null;

                    assembly = Assembly.Load(assemblyName);

                    // Replace the value with the loaded assembly
                    this.resolvedAssemblies[args.Name] = assembly;

                    return assembly;
                }
                finally
                {
                    if (null == assembly)
                    {
                        EqtTrace.Verbose("CurrentDomainAssemblyResolve: Failed to resolve assembly '{0}'.", args.Name);
                    }
                }
            }
        }
        
        /// <summary>
        /// Log the extensions
        /// </summary>
        private void LogExtensions()
        {
            if (EqtTrace.IsVerboseEnabled)
            {
                var discoverers = this.TestExtensions.TestDiscoverers != null ? string.Join(",", this.TestExtensions.TestDiscoverers.Keys.ToArray()) : null;
                EqtTrace.Verbose("TestPluginCache: Discoverers are '{0}'.", discoverers);

                var executors = this.TestExtensions.TestExecutors != null ? string.Join(",", this.TestExtensions.TestExecutors.Keys.ToArray()) : null;
                EqtTrace.Verbose("TestPluginCache: Executors are '{0}'.", executors);

                var settingsProviders = this.TestExtensions.TestSettingsProviders != null ? string.Join(",", this.TestExtensions.TestSettingsProviders.Keys.ToArray()) : null;
                EqtTrace.Verbose("TestPluginCache: Setting providers are '{0}'.", settingsProviders);

                var loggers = this.TestExtensions.TestLoggers != null ? string.Join(",", this.TestExtensions.TestLoggers.Keys.ToArray()) : null;
                EqtTrace.Verbose("TestPluginCache: Loggers are '{0}'.", loggers);
            }
        }

        #endregion
    }
}
