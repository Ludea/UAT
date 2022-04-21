// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicGames.Core;
using OpenTracing;
using OpenTracing.Util;
using UnrealBuildBase;

namespace UnrealBuildTool
{
	/// <summary>
	/// Options controlling how a target is built
	/// </summary>
	[Flags]
	enum BuildOptions
	{
		/// <summary>
		/// Default options
		/// </summary>
		None = 0,

		/// <summary>
		/// Don't build anything, just do target setup and terminate
		/// </summary>
		SkipBuild = 1,

		/// <summary>
		/// Just output a list of XGE actions; don't build anything
		/// </summary>
		XGEExport = 2,

		/// <summary>
		/// Fail if any engine files would be modified by the build
		/// </summary>
		NoEngineChanges = 4,
	}

	/// <summary>
	/// Builds a target
	/// </summary>
	[ToolMode("Build", ToolModeOptions.XmlConfig | ToolModeOptions.BuildPlatforms | ToolModeOptions.SingleInstance | ToolModeOptions.StartPrefetchingEngine | ToolModeOptions.ShowExecutionTime | ToolModeOptions.UseStartupTraceListener)]
	class BuildMode : ToolMode
	{
		/// <summary>
		/// Specifies the file to use for logging.
		/// </summary>
		[XmlConfigFile(Category = "BuildConfiguration")]
		public string? BaseLogFileName;

		/// <summary>
		/// Whether to skip checking for files identified by the junk manifest.
		/// </summary>
		[XmlConfigFile]
		[CommandLine("-IgnoreJunk")]
		public bool bIgnoreJunk = false;

		/// <summary>
		/// Skip building; just do setup and terminate.
		/// </summary>
		[CommandLine("-SkipBuild")]
		public bool bSkipBuild = false;

		/// <summary>
		/// Skip pre build targets; just do the main target.
		/// </summary>
		[CommandLine("-SkipPreBuildTargets")]
		public bool bSkipPreBuildTargets = false;

		/// <summary>
		/// Whether we should just export the XGE XML and pretend it succeeded
		/// </summary>
		[CommandLine("-XGEExport")]
		public bool bXGEExport = false;

		/// <summary>
		/// Do not allow any engine files to be output (used by compile on startup functionality)
		/// </summary>
		[CommandLine("-NoEngineChanges")]
		public bool bNoEngineChanges = false;

		/// <summary>
		/// Whether we should just export the outdated actions list
		/// </summary>
		[CommandLine("-WriteOutdatedActions=")]
		public FileReference? WriteOutdatedActionsFile = null;

		/// <summary>
		/// Main entry point
		/// </summary>
		/// <param name="Arguments">Command-line arguments</param>
		/// <returns>One of the values of ECompilationResult</returns>
		public override int Execute(CommandLineArguments Arguments)
		{
			Arguments.ApplyTo(this);

			// Write the command line
			Log.TraceLog("Command line: {0}", Environment.CommandLine);

			// Grab the environment.
			UnrealBuildTool.InitialEnvironment = Environment.GetEnvironmentVariables();
			if (UnrealBuildTool.InitialEnvironment.Count < 1)
			{
				throw new BuildException("Environment could not be read");
			}

			// Read the XML configuration files
			XmlConfig.ApplyTo(this);

			// Fixup the log path if it wasn't overridden by a config file
			if (BaseLogFileName == null)
			{
				BaseLogFileName = FileReference.Combine(UnrealBuildTool.EngineProgramSavedDirectory, "UnrealBuildTool", "Log.txt").FullName;
			}

			// Create the log file, and flush the startup listener to it
			if (!Arguments.HasOption("-NoLog") && !Log.HasFileWriter())
			{
				Log.AddFileWriter("DefaultLogTraceListener", FileReference.FromString(BaseLogFileName));
			}
			else
			{
				IEnumerable<StartupTraceListener> StartupListeners = Trace.Listeners.OfType<StartupTraceListener>();
				if (StartupListeners.Any())
				{
					Trace.Listeners.Remove(StartupListeners.First());
				}
			}

			// Create the build configuration object, and read the settings
			BuildConfiguration BuildConfiguration = new BuildConfiguration();
			XmlConfig.ApplyTo(BuildConfiguration);
			Arguments.ApplyTo(BuildConfiguration);

			// Check the root path length isn't too long
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64 && Unreal.RootDirectory.FullName.Length > BuildConfiguration.MaxRootPathLength)
			{
				Log.TraceWarning("Running from a path with a long directory name (\"{0}\" = {1} characters). Root paths shorter than {2} characters are recommended to avoid exceeding maximum path lengths on Windows.", Unreal.RootDirectory, Unreal.RootDirectory.FullName.Length, BuildConfiguration.MaxRootPathLength);
			}

			// now that we know the available platforms, we can delete other platforms' junk. if we're only building specific modules from the editor, don't touch anything else (it may be in use).
			if (!bIgnoreJunk && !Unreal.IsEngineInstalled())
			{
				using (GlobalTracer.Instance.BuildSpan("DeleteJunk()").StartActive())
				{
					JunkDeleter.DeleteJunk();
				}
			}

			// Parse and build the targets
			try
			{
				List<TargetDescriptor> TargetDescriptors;

				// Parse all the target descriptors
				using (GlobalTracer.Instance.BuildSpan("TargetDescriptor.ParseCommandLine()").StartActive())
				{
					TargetDescriptors = TargetDescriptor.ParseCommandLine(Arguments, BuildConfiguration.bUsePrecompiled, BuildConfiguration.bSkipRulesCompile, BuildConfiguration.bForceRulesCompile);
				}

				// Hack for specific files compile; don't build the ShaderCompileWorker target that's added to the command line for generated project files
				if(TargetDescriptors.Count >= 2)
				{
					TargetDescriptors.RemoveAll(x => (x.Name == "ShaderCompileWorker" || x.Name == "LiveCodingConsole" || x.Name == "InterchangeWorker") && x.SpecificFilesToCompile.Count > 0);
				}

				// Clean any target that wanted to be cleaned before being rebuilt
				if (TargetDescriptors.Any(D => D.bRebuild))
				{
					CleanMode CleanMode = new CleanMode();
					CleanMode.bSkipPreBuildTargets = bSkipPreBuildTargets;
					CleanMode.Clean(TargetDescriptors.Where(D => D.bRebuild).ToList(), BuildConfiguration);
				}


				// Handle remote builds
				for (int Idx = 0; Idx < TargetDescriptors.Count; ++Idx)
				{
					TargetDescriptor TargetDesc = TargetDescriptors[Idx];
					if (RemoteMac.HandlesTargetPlatform(TargetDesc.Platform))
					{
						FileReference BaseLogFile = Log.OutputFile ?? new FileReference(BaseLogFileName);
						FileReference RemoteLogFile = FileReference.Combine(BaseLogFile.Directory, BaseLogFile.GetFileNameWithoutExtension() + "_Remote.txt");

						RemoteMac RemoteMac = new RemoteMac(TargetDesc.ProjectFile);
						if (!RemoteMac.Build(TargetDesc, RemoteLogFile, bSkipPreBuildTargets))
						{
							return (int)CompilationResult.Unknown;
						}

						TargetDescriptors.RemoveAt(Idx--);
					}
				}

				// Handle local builds
				if (TargetDescriptors.Count > 0)
				{
					// Get a set of all the project directories
					HashSet<DirectoryReference> ProjectDirs = new HashSet<DirectoryReference>();
					foreach (TargetDescriptor TargetDesc in TargetDescriptors)
					{
						if (TargetDesc.ProjectFile != null)
						{
							DirectoryReference ProjectDirectory = TargetDesc.ProjectFile.Directory;
							FileMetadataPrefetch.QueueProjectDirectory(ProjectDirectory);
							ProjectDirs.Add(ProjectDirectory);
						}

						// print out SDK info for only the platforms that are being compiled
						UEBuildPlatformSDK.GetSDKForPlatform(TargetDesc.Platform.ToString())?.PrintSDKInfoAndReturnValidity();
					}

					// Get all the build options
					BuildOptions Options = BuildOptions.None;
					if (bSkipBuild)
					{
						Options |= BuildOptions.SkipBuild;
					}
					if (bXGEExport)
					{
						Options |= BuildOptions.XGEExport;
					}
					if(bNoEngineChanges)
					{
						Options |= BuildOptions.NoEngineChanges;
					}

					// Create the working set provider per group.
					using (ISourceFileWorkingSet WorkingSet = SourceFileWorkingSet.Create(Unreal.RootDirectory, ProjectDirs))
					{
						Build(TargetDescriptors, BuildConfiguration, WorkingSet, Options, WriteOutdatedActionsFile, bSkipPreBuildTargets);
					}
				}
			}
			finally
			{
				// Save all the caches
				SourceFileMetadataCache.SaveAll();
				CppDependencyCache.SaveAll();
			}
			return 0;
		}

		/// <summary>
		/// Build a list of targets
		/// </summary>
		/// <param name="TargetDescriptors">Target descriptors</param>
		/// <param name="BuildConfiguration">Current build configuration</param>
		/// <param name="WorkingSet">The source file working set</param>
		/// <param name="Options">Additional options for the build</param>
		/// <param name="WriteOutdatedActionsFile">Files to write the list of outdated actions to (rather than building them)</param>
		/// <param name="bSkipPreBuildTargets">If true then only the current target descriptors will be built.</param>
		/// <returns>Result from the compilation</returns>
		public static void Build(List<TargetDescriptor> TargetDescriptors, BuildConfiguration BuildConfiguration, ISourceFileWorkingSet WorkingSet, BuildOptions Options, FileReference? WriteOutdatedActionsFile, bool bSkipPreBuildTargets = false)
		{

			List<TargetMakefile> TargetMakefiles = new List<TargetMakefile>();

			for (int Idx = 0; Idx < TargetDescriptors.Count; ++Idx)
			{
				TargetMakefile NewMakefile = CreateMakefile(BuildConfiguration, TargetDescriptors[Idx], WorkingSet);
				TargetMakefiles.Add(NewMakefile);
				if (!bSkipPreBuildTargets)
				{
					foreach (TargetInfo PreBuildTarget in NewMakefile.PreBuildTargets)
					{
						TargetDescriptor NewTarget = TargetDescriptor.FromTargetInfo(PreBuildTarget);
						if (!TargetDescriptors.Contains(NewTarget))
						{
							TargetDescriptors.Add(NewTarget);
						}
					}
				}
			}

			Build(TargetMakefiles.ToArray(), TargetDescriptors, BuildConfiguration, Options, WriteOutdatedActionsFile);
		}

		/// <summary>
		/// Build a list of targets with a given set of makefiles.
		/// </summary>
		/// <param name="Makefiles">Makefiles created with CreateMakefiles</param>
		/// <param name="TargetDescriptors">Target descriptors</param>
		/// <param name="BuildConfiguration">Current build configuration</param>
		/// <param name="Options">Additional options for the build</param>
		/// <param name="WriteOutdatedActionsFile">Files to write the list of outdated actions to (rather than building them)</param>
		/// <returns>Result from the compilation</returns>
		static void Build(TargetMakefile[] Makefiles, List<TargetDescriptor> TargetDescriptors, BuildConfiguration BuildConfiguration, BuildOptions Options, FileReference? WriteOutdatedActionsFile)
		{
			// Execute the build
			if ((Options & BuildOptions.SkipBuild) == 0)
			{
				// Make sure that none of the actions conflict with any other (producing output files differently, etc...)
				ActionGraph.CheckForConflicts(Makefiles.SelectMany(x => x.Actions));

				// Check we don't exceed the nominal max path length
				using (GlobalTracer.Instance.BuildSpan("ActionGraph.CheckPathLengths").StartActive())
				{
					ActionGraph.CheckPathLengths(BuildConfiguration, Makefiles.SelectMany(x => x.Actions));
				}

				// Create a QueuedAction instance for each action in the makefiles
				List<LinkedAction>[] QueuedActions = new List<LinkedAction>[Makefiles.Length];
				for(int Idx = 0; Idx < Makefiles.Length; Idx++)
				{
					QueuedActions[Idx] = Makefiles[Idx].Actions.ConvertAll(x => new LinkedAction(x, TargetDescriptors[Idx]));
				}

				// Clean up any previous hot reload runs, and reapply the current state if it's already active
				Dictionary<FileReference, FileReference>? InitialPatchedOldLocationToNewLocation = null;
				for (int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; TargetIdx++)
				{
					InitialPatchedOldLocationToNewLocation = HotReload.Setup(TargetDescriptors[TargetIdx], Makefiles[TargetIdx], QueuedActions[TargetIdx], BuildConfiguration);
				}

				// Merge the action graphs together
				List<LinkedAction> MergedActions;
				if (TargetDescriptors.Count == 1)
				{
					MergedActions = QueuedActions[0];
				}
				else
				{
					MergedActions = MergeActionGraphs(TargetDescriptors, QueuedActions);
				}

				// Gather all the prerequisite actions that are part of the targets
				HashSet<FileItem> MergedOutputItems = new HashSet<FileItem>();
				for (int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; TargetIdx++)
				{
					GatherOutputItems(TargetDescriptors[TargetIdx], Makefiles[TargetIdx], MergedOutputItems);
				}

				// Link all the actions together
				ActionGraph.Link(MergedActions);

				// Get all the actions that are prerequisites for these targets. This forms the list of actions that we want executed.
				List<LinkedAction> PrerequisiteActions = ActionGraph.GatherPrerequisiteActions(MergedActions, MergedOutputItems);

				// Create the action history
				ActionHistory History = new ActionHistory();
				for (int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; TargetIdx++)
				{
					using (GlobalTracer.Instance.BuildSpan("Reading action history").StartActive())
					{
						TargetDescriptor TargetDescriptor = TargetDescriptors[TargetIdx];
						if(TargetDescriptor.ProjectFile != null)
						{
							History.Mount(TargetDescriptor.ProjectFile.Directory);
						}
					}
				}

				// Create the C++ dependency cache
				CppDependencyCache CppDependencies = new CppDependencyCache();
				for (int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; TargetIdx++)
				{
					using (GlobalTracer.Instance.BuildSpan("Reading dependency cache").StartActive())
					{
						TargetDescriptor TargetDescriptor = TargetDescriptors[TargetIdx];
						CppDependencies.Mount(TargetDescriptor.ProjectFile, TargetDescriptor.Name, TargetDescriptor.Platform, TargetDescriptor.Configuration, Makefiles[TargetIdx].TargetType, TargetDescriptor.Architecture);
					}
				}

				// Pre-process module interfaces to generate dependency files
				List<LinkedAction> ModuleDependencyActions = PrerequisiteActions.Where(x => x.ActionType == ActionType.GatherModuleDependencies).ToList();
				if (ModuleDependencyActions.Count > 0)
				{
					Dictionary<LinkedAction, bool> PreprocessActionToOutdatedFlag = new Dictionary<LinkedAction, bool>();
					ActionGraph.GatherAllOutdatedActions(ModuleDependencyActions, History, PreprocessActionToOutdatedFlag, CppDependencies, BuildConfiguration.bIgnoreOutdatedImportLibraries);

					List<LinkedAction> PreprocessActions = PreprocessActionToOutdatedFlag.Where(x => x.Value).Select(x => x.Key).ToList();
					if (PreprocessActions.Count > 0)
					{
						Log.TraceInformation("Updating module dependencies...");
						ActionGraph.ExecuteActions(BuildConfiguration, PreprocessActions);

						foreach (FileItem ProducedItem in PreprocessActions.SelectMany(x => x.ProducedItems))
						{
							ProducedItem.ResetCachedInfo();
						}
					}
				}

				// Figure out which actions need to be built
				Dictionary<LinkedAction, bool> ActionToOutdatedFlag = new Dictionary<LinkedAction, bool>();
				for (int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; TargetIdx++)
				{
					TargetDescriptor TargetDescriptor = TargetDescriptors[TargetIdx];

					// Update the module dependencies
					Dictionary<string, FileItem> ModuleOutputs = new Dictionary<string, FileItem>(StringComparer.Ordinal);
					if (ModuleDependencyActions.Count > 0)
					{
						Dictionary<FileItem, List<string>> ModuleImports = new Dictionary<FileItem, List<string>>();

						List<FileItem> CompiledModuleInterfaces = new List<FileItem>();
						foreach(LinkedAction ModuleDependencyAction in ModuleDependencyActions)
						{
							ICppCompileAction? CppModulesAction = ModuleDependencyAction.Inner as ICppCompileAction;
							if (CppModulesAction != null && CppModulesAction.CompiledModuleInterfaceFile != null)
							{
								string? ProducedModule;
								if (CppDependencies.TryGetProducedModule(ModuleDependencyAction.DependencyListFile!, out ProducedModule))
								{
									ModuleOutputs[ProducedModule] = CppModulesAction.CompiledModuleInterfaceFile;
								}

								List<(string Name, string BMI)>? ImportedModules;
								if (CppDependencies.TryGetImportedModules(ModuleDependencyAction.DependencyListFile!, out ImportedModules))
								{
									ModuleImports[CppModulesAction.CompiledModuleInterfaceFile] = ImportedModules.Select(x => x.Name).ToList();
								}

								CompiledModuleInterfaces.Add(CppModulesAction.CompiledModuleInterfaceFile);
							}
						}

						Dictionary<FileItem, string> ModuleInputs = ModuleOutputs.ToDictionary(x => x.Value, x => x.Key);
						foreach (LinkedAction PrerequisiteAction in PrerequisiteActions)
						{
							if (PrerequisiteAction.ActionType == ActionType.CompileModuleInterface)
							{
								ICppCompileAction CppModulesAction = (ICppCompileAction)PrerequisiteAction.Inner;

								List<string>? ImportedModules;
								if(ModuleImports.TryGetValue(CppModulesAction.CompiledModuleInterfaceFile!, out ImportedModules))
								{
									foreach (string ImportedModule in ImportedModules)
									{
										FileItem? ModuleOutput;
										if (ModuleOutputs.TryGetValue(ImportedModule, out ModuleOutput))
										{
											Action NewAction = new Action(PrerequisiteAction.Inner);
											NewAction.PrerequisiteItems.Add(ModuleOutput);
											NewAction.CommandArguments += String.Format(" /reference \"{0}={1}\"", ImportedModule, ModuleOutput.FullName);
											PrerequisiteAction.Inner = NewAction;
										}
										else
										{
											throw new BuildException("Unable to find interface for module '{0}'", ImportedModule);
										}
									}
								}
							}
							else if(PrerequisiteAction.ActionType == ActionType.Compile)
							{
								foreach(FileItem PrerequisiteItem in PrerequisiteAction.PrerequisiteItems)
								{
									string? ModuleName;
									if(ModuleInputs.TryGetValue(PrerequisiteItem, out ModuleName))
									{
										Action NewAction = new Action(PrerequisiteAction.Inner);
										NewAction.CommandArguments += String.Format(" /reference \"{0}={1}\"", ModuleName, PrerequisiteItem.AbsolutePath);
										PrerequisiteAction.Inner = NewAction;
									}
								}
							}
							else
							{
								if (PrerequisiteAction.ActionType != ActionType.GatherModuleDependencies)
								{
									Action NewAction = new Action(PrerequisiteAction.Inner);
									NewAction.PrerequisiteItems.AddRange(CompiledModuleInterfaces);
									PrerequisiteAction.Inner = NewAction;
								}
							}
						}
					}

					// Plan the actions to execute for the build. For single file compiles, always rebuild the source file regardless of whether it's out of date.
					if (TargetDescriptor.SpecificFilesToCompile.Count == 0)
					{
						ActionGraph.GatherAllOutdatedActions(PrerequisiteActions, History, ActionToOutdatedFlag, CppDependencies, BuildConfiguration.bIgnoreOutdatedImportLibraries);
					}
					else
					{
						foreach (FileReference SpecificFile in TargetDescriptor.SpecificFilesToCompile)
						{
							foreach (LinkedAction PrerequisiteAction in PrerequisiteActions.Where(x => x.PrerequisiteItems.Any(y => y.Location == SpecificFile)))
							{
								ActionToOutdatedFlag[PrerequisiteAction] = true;
							}
						}
					}
				}

				// Link the action graph again to sort it
				List<LinkedAction> MergedActionsToExecute = ActionToOutdatedFlag.Where(x => x.Value).Select(x => x.Key).ToList();
				ActionGraph.Link(MergedActionsToExecute);

				// Allow hot reload to override the actions
				int HotReloadTargetIdx = -1;
				for(int Idx = 0; Idx < TargetDescriptors.Count; Idx++)
				{
					if (TargetDescriptors[Idx].HotReloadMode != HotReloadMode.Disabled)
					{
						if (HotReloadTargetIdx != -1)
						{
							throw new BuildException("Unable to perform hot reload with multiple targets.");
						}
						else
						{
							MergedActionsToExecute = HotReload.PatchActionsForTarget(BuildConfiguration, TargetDescriptors[Idx], Makefiles[Idx], PrerequisiteActions, MergedActionsToExecute, InitialPatchedOldLocationToNewLocation);
						}
						HotReloadTargetIdx = Idx;
					}
					else if (MergedActionsToExecute.Count > 0)
					{
						HotReload.CheckForLiveCodingSessionActive(TargetDescriptors[Idx], Makefiles[Idx], BuildConfiguration);
					}
				}	

				if (HotReloadTargetIdx != -1)
				{
					Log.TraceLog("Re-evaluating action graph");
					// Re-check the graph to remove any LiveCoding actions added by PatchActionsForTarget() that are already up to date.
					Dictionary<LinkedAction, bool> LiveActionToOutdatedFlag = new Dictionary<LinkedAction, bool>(MergedActionsToExecute.Count);
					ActionGraph.GatherAllOutdatedActions(MergedActionsToExecute, History, LiveActionToOutdatedFlag, CppDependencies, BuildConfiguration.bIgnoreOutdatedImportLibraries);
					List<LinkedAction> LiveCodingActionsToExecute = LiveActionToOutdatedFlag.Where(x => x.Value).Select(x => x.Key).ToList();
					ActionGraph.Link(LiveCodingActionsToExecute);
					MergedActionsToExecute = LiveCodingActionsToExecute;
				}

				// Make sure we're not modifying any engine files
				if ((Options & BuildOptions.NoEngineChanges) != 0)
				{
					List<FileItem> EngineChanges = MergedActionsToExecute.SelectMany(x => x.ProducedItems).Where(x => x.Location.IsUnderDirectory(Unreal.EngineDirectory)).Distinct().OrderBy(x => x.FullName).ToList();
					if (EngineChanges.Count > 0)
					{
						StringBuilder Result = new StringBuilder("Building would modify the following engine files:\n");
						foreach (FileItem EngineChange in EngineChanges)
						{
							Result.AppendFormat("\n{0}", EngineChange.FullName);
						}
						Result.Append("\n\nPlease rebuild from an IDE instead.");
						Log.TraceError("{0}", Result.ToString());
						throw new CompilationResultException(CompilationResult.FailedDueToEngineChange);
					}
				}

				// Make sure the appropriate executor is selected
				foreach (TargetDescriptor TargetDescriptor in TargetDescriptors)
				{
					UEBuildPlatform BuildPlatform = UEBuildPlatform.GetBuildPlatform(TargetDescriptor.Platform);
					BuildConfiguration.bAllowXGE &= BuildPlatform.CanUseXGE();
					BuildConfiguration.bAllowFASTBuild &= BuildPlatform.CanUseFASTBuild();
					BuildConfiguration.bAllowSNDBS &= BuildPlatform.CanUseSNDBS();
				}

				// Delete produced items that are outdated.
				ActionGraph.DeleteOutdatedProducedItems(MergedActionsToExecute);

				// Save all the action histories now that files have been removed. We have to do this after deleting produced items to ensure that any
				// items created during the build don't have the wrong command line.
				History.Save();

				// Create directories for the outdated produced items.
				ActionGraph.CreateDirectoriesForProducedItems(MergedActionsToExecute);

				// Execute the actions
				if ((Options & BuildOptions.XGEExport) != 0)
				{
					OutputToolchainInfo(TargetDescriptors, Makefiles);

					// Just export to an XML file
					using (GlobalTracer.Instance.BuildSpan("XGE.ExportActions()").StartActive())
					{
						XGE.ExportActions(MergedActionsToExecute);
					}
				}
				else if(WriteOutdatedActionsFile != null)
				{
					OutputToolchainInfo(TargetDescriptors, Makefiles);

					// Write actions to an output file
					using (GlobalTracer.Instance.BuildSpan("ActionGraph.WriteActions").StartActive())
					{
						ActionGraph.ExportJson(MergedActionsToExecute, WriteOutdatedActionsFile);
					}
				}
				else
				{
					// Execute the actions
					if(MergedActionsToExecute.Count == 0)
					{
						if (TargetDescriptors.Any(x => !x.bQuiet))
						{
							Log.TraceInformation((TargetDescriptors.Count == 1)? "Target is up to date" : "Targets are up to date");
						}
					}
					else
					{
						if (TargetDescriptors.Any(x => !x.bQuiet))
						{
							Log.TraceInformation("Building {0}...", StringUtils.FormatList(TargetDescriptors.Select(x => x.Name).Distinct()));
						}

						OutputToolchainInfo(TargetDescriptors, Makefiles);

						ActionExecutor.SetMemoryPerActionOverride(Makefiles.Select(x => x.MemoryPerActionGB).Max());

						using (GlobalTracer.Instance.BuildSpan("ActionGraph.ExecuteActions()").StartActive())
						{
							ActionGraph.ExecuteActions(BuildConfiguration, MergedActionsToExecute);
						}
					}

					// Run the deployment steps
					foreach(TargetMakefile Makefile in Makefiles)
					{
						if (Makefile.bDeployAfterCompile)
						{
							TargetReceipt Receipt = TargetReceipt.Read(Makefile.ReceiptFile);
							Log.TraceInformation("Deploying {0} {1} {2}...", Receipt.TargetName, Receipt.Platform, Receipt.Configuration);

							UEBuildPlatform.GetBuildPlatform(Receipt.Platform).Deploy(Receipt);
						}
					}
				}
			}
		}

		/// <summary>
		/// Outputs the toolchain used to build each target
		/// </summary>
		/// <param name="TargetDescriptors">List of targets being built</param>
		/// <param name="Makefiles">Matching array of makefiles for each target</param>
		static void OutputToolchainInfo(List<TargetDescriptor> TargetDescriptors, TargetMakefile[] Makefiles)
		{
			List<int> OutputIndices = new List<int>();
			for (int Idx = 0; Idx < TargetDescriptors.Count; Idx++)
			{
				if (!TargetDescriptors[Idx].bQuiet)
				{
					OutputIndices.Add(Idx);
				}
			}

			if(OutputIndices.Count == 1)
			{
				foreach(string Diagnostic in Makefiles[OutputIndices[0]].Diagnostics)
				{
					Log.TraceInformation("{0}", Diagnostic);
				}
			}
			else
			{
				foreach(int OutputIndex in OutputIndices)
				{
					foreach(string Diagnostic in Makefiles[OutputIndex].Diagnostics)
					{
						Log.TraceInformation("{0}: {1}", TargetDescriptors[OutputIndex].Name, Diagnostic);
					}
				}
			}
		}

		/// <summary>
		/// Creates the makefile for a target. If an existing, valid makefile already exists on disk, loads that instead.
		/// </summary>
		/// <param name="BuildConfiguration">The build configuration</param>
		/// <param name="TargetDescriptor">Target being built</param>
		/// <param name="WorkingSet">Set of source files which are part of the working set</param>
		/// <returns>Makefile for the given target</returns>
		static TargetMakefile CreateMakefile(BuildConfiguration BuildConfiguration, TargetDescriptor TargetDescriptor, ISourceFileWorkingSet WorkingSet)
		{
			// Get the path to the makefile for this target
			FileReference? MakefileLocation = null;
			if(BuildConfiguration.bUseUBTMakefiles && TargetDescriptor.SpecificFilesToCompile.Count == 0)
			{
				MakefileLocation = TargetMakefile.GetLocation(TargetDescriptor.ProjectFile, TargetDescriptor.Name, TargetDescriptor.Platform, TargetDescriptor.Architecture, TargetDescriptor.Configuration);
			}

			// Try to load an existing makefile
			TargetMakefile? Makefile = null;
			if(MakefileLocation != null)
			{
				using (GlobalTracer.Instance.BuildSpan("TargetMakefile.Load()").StartActive())
				{
					string? ReasonNotLoaded;
					Makefile = TargetMakefile.Load(MakefileLocation, TargetDescriptor.ProjectFile, TargetDescriptor.Platform, TargetDescriptor.AdditionalArguments.GetRawArray(), out ReasonNotLoaded);
					if (Makefile == null)
					{
						Log.TraceInformation("Creating makefile for {0} ({1})", TargetDescriptor.Name, ReasonNotLoaded);
					}
				}
			}

			// If we have a makefile, execute the pre-build steps and check it's still valid
			bool bHasRunPreBuildScripts = false;
			if(Makefile != null)
			{
				// Execute the scripts. We have to invalidate all cached file info after doing so, because we don't know what may have changed.
				if(Makefile.PreBuildScripts.Length > 0)
				{
					Utils.ExecuteCustomBuildSteps(Makefile.PreBuildScripts);
					DirectoryItem.ResetAllCachedInfo_SLOW();
				}

				// Don't run the pre-build steps again, even if we invalidate the makefile.
				bHasRunPreBuildScripts = true;

				// Check that the makefile is still valid
				string? Reason;
				if(!TargetMakefile.IsValidForSourceFiles(Makefile, TargetDescriptor.ProjectFile, TargetDescriptor.Platform, WorkingSet, out Reason))
				{
					Log.TraceInformation("Invalidating makefile for {0} ({1})", TargetDescriptor.Name, Reason);
					Makefile = null;
				}
			}

			// If we couldn't load a makefile, create a new one
			if(Makefile == null)
			{
				// Create the target
				UEBuildTarget Target;
				using (GlobalTracer.Instance.BuildSpan("UEBuildTarget.Create()").StartActive())
				{
					Target = UEBuildTarget.Create(TargetDescriptor, BuildConfiguration.bSkipRulesCompile, BuildConfiguration.bForceRulesCompile, BuildConfiguration.bUsePrecompiled);
				}

				// Create the pre-build scripts
				FileReference[] PreBuildScripts = Target.CreatePreBuildScripts();

				// Execute the pre-build scripts
				if(!bHasRunPreBuildScripts)
				{
					Utils.ExecuteCustomBuildSteps(PreBuildScripts);
					bHasRunPreBuildScripts = true;
				}

				// Build the target
				using (GlobalTracer.Instance.BuildSpan("UEBuildTarget.Build()").StartActive())
				{
					Makefile = Target.Build(BuildConfiguration, WorkingSet, TargetDescriptor);
				}

				Makefile.MemoryPerActionGB = Target.Rules.MemoryPerActionGB;

				// Save the pre-build scripts onto the makefile
				Makefile.PreBuildScripts = PreBuildScripts;

				// Save the additional command line arguments
				Makefile.AdditionalArguments = TargetDescriptor.AdditionalArguments.GetRawArray();

				// Save the environment variables
				foreach (System.Collections.DictionaryEntry? EnvironmentVariable in Environment.GetEnvironmentVariables())
				{
					if (EnvironmentVariable != null)
					{
						Makefile.EnvironmentVariables.Add(Tuple.Create((string)EnvironmentVariable.Value.Key, (string)EnvironmentVariable.Value.Value!));
					}
				}

				// Save the makefile for next time
				if(MakefileLocation != null)
				{
					using (GlobalTracer.Instance.BuildSpan("TargetMakefile.Save()").StartActive())
					{
						Makefile.Save(MakefileLocation);
					}
				}
			}
			else
			{
				// Restore the environment variables
				foreach (Tuple<string, string> EnvironmentVariable in Makefile.EnvironmentVariables)
				{
					Environment.SetEnvironmentVariable(EnvironmentVariable.Item1, EnvironmentVariable.Item2);
				}

				// If the target needs UHT to be run, we'll go ahead and do that now
				if (Makefile.UObjectModules.Count > 0)
				{
					ExternalExecution.ExecuteHeaderToolIfNecessary(BuildConfiguration, TargetDescriptor.ProjectFile, Makefile, TargetDescriptor.Name, WorkingSet);
				}

#if __VPROJECT_AVAILABLE__
				// Same for VNI
				if (Makefile.VNIModules.Count > 0)
				{

					VNIExecution.ExecuteVNITool(Makefile, TargetDescriptor);
				}
#endif
			}
			return Makefile;
		}

		/// <summary>
		/// Determines all the actions that should be executed for a target (filtering for single module/file, etc..)
		/// </summary>
		/// <param name="TargetDescriptor">The target being built</param>
		/// <param name="Makefile">Makefile for the target</param>
		/// <param name="OutputItems">Set of all output items</param>
		/// <returns>List of actions that need to be executed</returns>
		static void GatherOutputItems(TargetDescriptor TargetDescriptor, TargetMakefile Makefile, HashSet<FileItem> OutputItems)
		{
			if(TargetDescriptor.SpecificFilesToCompile.Count > 0)
			{
				// If we're just compiling a specific files, set the target items to be all the derived items
				List<FileItem> FilesToCompile = TargetDescriptor.SpecificFilesToCompile.ConvertAll(x => FileItem.GetItemByFileReference(x));
				OutputItems.UnionWith(
					Makefile.Actions.Where(x => x.PrerequisiteItems.Any(y => FilesToCompile.Contains(y)))
					.SelectMany(x => x.ProducedItems));
			}
			else if(TargetDescriptor.OnlyModuleNames.Count > 0)
			{
				// Find the output items for this module
				foreach(string OnlyModuleName in TargetDescriptor.OnlyModuleNames)
				{
					FileItem[]? OutputItemsForModule;
					if(!Makefile.ModuleNameToOutputItems.TryGetValue(OnlyModuleName, out OutputItemsForModule))
					{
						throw new BuildException("Unable to find output items for module '{0}'", OnlyModuleName);
					}
					OutputItems.UnionWith(OutputItemsForModule);
				}
			}
			else
			{
				// Use all the output items from the target
				OutputItems.UnionWith(Makefile.OutputItems);
			}
		}

		/// <summary>
		/// Merge action graphs for multiple targets into a single set of actions. Sets group names on merged actions to indicate which target they belong to.
		/// </summary>
		/// <param name="TargetDescriptors">List of target descriptors</param>
		/// <param name="TargetActions">Array of actions for each target</param>
		/// <returns>List of merged actions</returns>
		static List<LinkedAction> MergeActionGraphs(List<TargetDescriptor> TargetDescriptors, List<LinkedAction>[] TargetActions)
		{
			// Set of all output items. Knowing that there are no conflicts in produced items, we use this to eliminate duplicate actions.
			Dictionary<FileItem, LinkedAction> OutputItemToProducingAction = new Dictionary<FileItem, LinkedAction>();
			for(int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; TargetIdx++)
			{
				string GroupPrefix = String.Format("{0}-{1}-{2}", TargetDescriptors[TargetIdx].Name, TargetDescriptors[TargetIdx].Platform, TargetDescriptors[TargetIdx].Configuration);
				foreach(LinkedAction TargetAction in TargetActions[TargetIdx])
				{
					FileItem ProducedItem = TargetAction.ProducedItems.First();

					LinkedAction? ExistingAction;
					if(!OutputItemToProducingAction.TryGetValue(ProducedItem, out ExistingAction))
					{
						ExistingAction = new LinkedAction(TargetAction, TargetDescriptors[TargetIdx]);
						OutputItemToProducingAction[ProducedItem] = ExistingAction;
					}
					ExistingAction.GroupNames.Add(GroupPrefix);
				}
			}
			return new List<LinkedAction>(OutputItemToProducingAction.Values);
		}
	}
}

