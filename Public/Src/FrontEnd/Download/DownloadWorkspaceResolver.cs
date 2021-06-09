// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.FrontEnd.Download.Tracing;
using BuildXL.FrontEnd.Sdk;
using BuildXL.FrontEnd.Workspaces;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using JetBrains.Annotations;
using TypeScript.Net.DScript;
using TypeScript.Net.Types;
using ValueTaskFactory = BuildXL.Utilities.Tasks.ValueTaskFactory;

namespace BuildXL.FrontEnd.Download
{
    /// <summary>
    /// A workspace module resolver that can download and extract archives.
    /// </summary>
    public sealed class DownloadWorkspaceResolver : IWorkspaceModuleResolver
    {
        /// <inheritdoc />
        public string Kind => "Download";

        /// <inheritdoc />
        public string Name { get; private set; }

        // These are set during Initialize
        private FrontEndContext m_context;

        /// <nodoc />
        public IReadOnlyDictionary<string, DownloadData> Downloads { get; private set; }

        private readonly HashSet<ModuleDescriptor> m_descriptors;
        private readonly MultiValueDictionary<string, ModuleDescriptor> m_descriptorsByName;
        private readonly Dictionary<AbsolutePath, ModuleDescriptor> m_descriptorsBySpecPath;
        private readonly Dictionary<ModuleDescriptor, ModuleDefinition> m_definitions;

        /// <nodoc/>
        public DownloadWorkspaceResolver()
        {
            m_descriptors = new HashSet<ModuleDescriptor>();
            m_descriptorsByName = new MultiValueDictionary<string, ModuleDescriptor>(StringComparer.Ordinal);
            m_descriptorsBySpecPath = new Dictionary<AbsolutePath, ModuleDescriptor>();
            m_definitions = new Dictionary<ModuleDescriptor, ModuleDefinition>();
        }

        /// <inheritdoc />
        public bool TryInitialize([NotNull] FrontEndHost host, [NotNull] FrontEndContext context, [NotNull] IConfiguration configuration, [NotNull] IResolverSettings resolverSettings)
        {
            Contract.Requires(context != null);
            Contract.Requires(host != null);
            Contract.Requires(configuration != null);
            Contract.Requires(resolverSettings != null);

            var settings = resolverSettings as IDownloadResolverSettings;
            Contract.Assert(settings != null);

            m_context = context;
            Name = resolverSettings.Name;

            var resolverFolder = host.GetFolderForFrontEnd(resolverSettings.Name ?? Kind);

            var downloads = new Dictionary<string, DownloadData>(StringComparer.Ordinal);
            foreach (var downloadSettings in settings.Downloads)
            {
                if (!ValidateAndExtractDownloadData(context, downloadSettings, downloads, resolverFolder, out var downloadData))
                {
                    return false;
                }

                downloads.Add(downloadSettings.ModuleName, downloadData);
                UpdateDataForDownloadData(downloadData);
            }

            Downloads = downloads;

            return true;
        }

        private bool ValidateAndExtractDownloadData(
            FrontEndContext context,
            IDownloadFileSettings downloadSettings,
            Dictionary<string, DownloadData> downloads,
            AbsolutePath resolverFolder,
            out DownloadData downloadData)
        {
            downloadData = null;
            if (string.IsNullOrEmpty(downloadSettings.ModuleName))
            {
                Logger.Log.DownloadFrontendMissingModuleId(m_context.LoggingContext, downloadSettings.Url);
                return false;
            }

            if (downloads.ContainsKey(downloadSettings.ModuleName))
            {
                Logger.Log.DownloadFrontendDuplicateModuleId(m_context.LoggingContext, downloadSettings.ModuleName, Kind, Name);
                return false;
            }

            if (string.IsNullOrEmpty(downloadSettings.Url))
            {
                Logger.Log.DownloadFrontendMissingUrl(m_context.LoggingContext, downloadSettings.ModuleName);
                return false;
            }

            if (!Uri.TryCreate(downloadSettings.Url, UriKind.Absolute, out var downloadLocation))
            {
                Logger.Log.DownloadFrontendInvalidUrl(m_context.LoggingContext, downloadSettings.ModuleName, downloadSettings.Url);
                return false;
            }

            ContentHash? contentHash;
            if (string.IsNullOrEmpty(downloadSettings.Hash))
            {
                contentHash = null;
            }
            else
            {
                if (!ContentHash.TryParse(downloadSettings.Hash, out var hash))
                {
                    Logger.Log.DownloadFrontendHashValueNotValidContentHash(m_context.LoggingContext, downloadSettings.ModuleName, downloadSettings.Url, downloadSettings.Hash);
                    return false;
                }

                contentHash = hash;
            }

            // For these last two validations, the TS parser would also complain, but we just give a more targeted error before that happens.
            if (!string.IsNullOrEmpty(downloadSettings.DownloadedValueName) && !SymbolAtom.TryCreate(context.StringTable, downloadSettings.DownloadedValueName, out _))
            {
                Logger.Log.NameContainsInvalidCharacters(m_context.LoggingContext, "downloadedValueName", downloadSettings.DownloadedValueName);
                return false;
            }

            if (!string.IsNullOrEmpty(downloadSettings.ExtractedValueName) && !SymbolAtom.TryCreate(context.StringTable, downloadSettings.ExtractedValueName, out _))
            {
                Logger.Log.NameContainsInvalidCharacters(m_context.LoggingContext, "extractedValueName", downloadSettings.ExtractedValueName);
                return false;
            }

            downloadData = new DownloadData(context, downloadSettings, downloadLocation, resolverFolder, contentHash, downloadSettings.DownloadedValueName, downloadSettings.ExtractedValueName);
            return true;
        }

        /// <summary>
        /// Returns the module descriptor for the download data.
        /// </summary>
        internal ModuleDescriptor GetModuleDescriptor(DownloadData downloadData)
        {
            return m_descriptorsBySpecPath[downloadData.ModuleSpecFile];
        }

        /// <inheritdoc />
        public string DescribeExtent()
        {
            Contract.Assume(m_descriptors != null, "Init must have been called");

            return string.Join(", ", m_descriptors.Select(descriptor => descriptor.Name));
        }

        /// <inheritdoc />
        public ValueTask<Possible<HashSet<ModuleDescriptor>>> GetAllKnownModuleDescriptorsAsync()
        {
            Contract.Assume(m_descriptors != null, "Init must have been called");

            return new ValueTask<Possible<HashSet<ModuleDescriptor>>>(m_descriptors);
        }

        /// <inheritdoc />
        public ISourceFile[] GetAllModuleConfigurationFiles()
        {
            // No need to do anything, this is for when input files are changed which should not happen for the Download resolver since the only data comes from config.
            return CollectionUtilities.EmptyArray<ISourceFile>();
        }

        /// <inheritdoc />
        public Task ReinitializeResolver()
        {
            // No need to do anything, this is for when input files are changed which should not happen for the Download resolver since the only data comes from config.
            return Task.FromResult<object>(null);
        }

        /// <inheritdoc />
        public ValueTask<Possible<ModuleDefinition>> TryGetModuleDefinitionAsync(ModuleDescriptor moduleDescriptor)
        {
            Contract.Assume(m_definitions != null, "Init must have been called");

            if (m_definitions.TryGetValue(moduleDescriptor, out var result))
            {
                return ValueTaskFactory.FromResult(Possible.Create(result));
            }

            return ValueTaskFactory.FromResult((Possible<ModuleDefinition>)new ModuleNotOwnedByThisResolver(moduleDescriptor));
        }

        /// <inheritdoc />
        public ValueTask<Possible<IReadOnlyCollection<ModuleDescriptor>>> TryGetModuleDescriptorsAsync(ModuleReferenceWithProvenance moduleReference)
        {
            Contract.Assume(m_descriptorsByName != null, "Init must have been called");

            IReadOnlyCollection<ModuleDescriptor> result;
            if (m_descriptorsByName.TryGetValue(moduleReference.Name, out var descriptors))
            {
                result = descriptors;
            }
            else
            {
                result = CollectionUtilities.EmptyArray<ModuleDescriptor>();
            }

            return ValueTaskFactory.FromResult(Possible.Create(result));
        }

        /// <inheritdoc />
        public ValueTask<Possible<ModuleDescriptor>> TryGetOwningModuleDescriptorAsync(AbsolutePath specPath)
        {
            Contract.Assume(m_descriptorsBySpecPath != null, "Init must have been called");

            if (m_descriptorsBySpecPath.TryGetValue(specPath, out var result))
            {
                return ValueTaskFactory.FromResult(Possible.Create(result));
            }

            var notOwnedFailure = new SpecNotOwnedByResolverFailure(specPath.ToString(m_context.PathTable));
            return ValueTaskFactory.FromResult((Possible<ModuleDescriptor>)notOwnedFailure);
        }

        /// <inheritdoc />
        public Task<Possible<ISourceFile>> TryParseAsync(AbsolutePath pathToParse, AbsolutePath moduleOrConfigPathPromptingParse, ParsingOptions parsingOption = null)
        {
            Contract.Assume(m_descriptorsBySpecPath != null, "Init must have been called");

            var pathToParseAsString = pathToParse.ToString(m_context.PathTable);

            if (!m_descriptorsBySpecPath.TryGetValue(pathToParse, out var descriptor))
            {
                return Task.FromResult<Possible<ISourceFile>>(new SpecNotOwnedByResolverFailure(pathToParseAsString));
            }

            if (!Downloads.TryGetValue(descriptor.Name, out var downloadData))
            {
                Contract.Assert(false, "Inconsistent internal state of NugetWorkspaceResolver");
                return Task.FromResult<Possible<ISourceFile>>(new SpecNotOwnedByResolverFailure(pathToParseAsString));
            }

            var sourceFile = SourceFile.Create(pathToParseAsString);

            var downloadDeclarationType = new TypeReferenceNode("File");
            downloadDeclarationType.TypeName.Pos = 1;
            downloadDeclarationType.TypeName.End = 2;
            var downloadDeclaration = new VariableDeclaration(downloadData.DownloadedValueName, Identifier.CreateUndefined(), downloadDeclarationType);
            downloadDeclaration.Flags |= NodeFlags.Export | NodeFlags.Public | NodeFlags.ScriptPublic;
            downloadDeclaration.Pos = 1;
            downloadDeclaration.End = 2;

            var extractedDeclarationType = new TypeReferenceNode("StaticDirectory");
            extractedDeclarationType.TypeName.Pos = 3;
            extractedDeclarationType.TypeName.Pos = 4;
            var extractedDeclaration = new VariableDeclaration(downloadData.ExtractedValueName, Identifier.CreateUndefined(), extractedDeclarationType);
            extractedDeclaration.Flags |= NodeFlags.Export | NodeFlags.Public | NodeFlags.ScriptPublic;
            extractedDeclaration.Pos = 3;
            extractedDeclaration.End = 4;

            sourceFile.Statements.Add(
                new VariableStatement()
                {
                    DeclarationList = new VariableDeclarationList(
                        NodeFlags.Const,
                        downloadDeclaration,
                        extractedDeclaration)
                }
            );

            // Needed for the binder to recurse.
            sourceFile.ExternalModuleIndicator = sourceFile;
            sourceFile.SetLineMap(new [] { 0, 2} );

            return Task.FromResult<Possible<ISourceFile>>(sourceFile);
        }

        internal void UpdateDataForDownloadData(DownloadData downloadData, FrontEndContext context = null)
        {
            context = context ?? m_context;
            Contract.Assert(context != null);

            var name = downloadData.Settings.ModuleName;

            var moduleId = ModuleId.Create(context.StringTable, name);
            var descriptor = new ModuleDescriptor(moduleId, name, name, string.Empty, Kind, Name);

            var definition = ModuleDefinition.CreateModuleDefinitionWithImplicitReferences(
                descriptor,
                downloadData.ModuleRoot,
                downloadData.ModuleConfigFile,
                new[] { downloadData.ModuleSpecFile },
                allowedModuleDependencies: null,
                cyclicalFriendModules: null,
                mounts: null); // A Download package does not have any module dependency restrictions nor allowlist cycles

            m_descriptors.Add(descriptor);
            m_descriptorsByName.Add(name, descriptor);
            m_descriptorsBySpecPath.Add(downloadData.ModuleSpecFile, descriptor);
            m_definitions.Add(descriptor, definition);
        }
    }
}
