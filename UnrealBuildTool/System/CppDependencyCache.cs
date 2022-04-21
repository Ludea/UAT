// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EpicGames.Core;
using UnrealBuildBase;

namespace UnrealBuildTool
{
	/// <summary>
	/// Reads the contents of C++ dependency files, and caches them for future iterations.
	/// </summary>
	class CppDependencyCache
	{
		/// <summary>
		/// Contents of a single dependency file
		/// </summary>
		class DependencyInfo
		{
			public long LastWriteTimeUtc;
			public string? ProducedModule;
			public List<(string Name, string BMI)>? ImportedModules;
			public List<FileItem> Files;

			public DependencyInfo(long LastWriteTimeUtc, string? ProducedModule, List<(string, string)>? ImportedModules, List<FileItem> Files)
			{
				this.LastWriteTimeUtc = LastWriteTimeUtc;
				this.ProducedModule = ProducedModule;
				this.ImportedModules = ImportedModules;
				this.Files = Files;
			}

			public static DependencyInfo Read(BinaryArchiveReader Reader)
			{
				long LastWriteTimeUtc = Reader.ReadLong();
				string? ProducedModule = Reader.ReadString();
				List<(string, string)>? ImportedModules = Reader.ReadList(() =>
				{
					return (Reader.ReadString(), Reader.ReadString());
				})!;
				List<FileItem> Files = Reader.ReadList(() => Reader.ReadCompactFileItem())!;
				return new DependencyInfo(LastWriteTimeUtc, ProducedModule, ImportedModules, Files);
			}

			public void Write(BinaryArchiveWriter Writer)
			{
				Writer.WriteLong(LastWriteTimeUtc);
				Writer.WriteString(ProducedModule);
				Writer.WriteList(ImportedModules, (Module) =>
				{
					Writer.WriteString(Module.Name);
					Writer.WriteString(Module.BMI);
				});
				Writer.WriteList<FileItem>(Files, File => Writer.WriteCompactFileItem(File));
			}
		}

		class CachePartition
		{
			/// <summary>
			/// The current file version
			/// </summary>
			public const int CurrentVersion = 3;

			/// <summary>
			/// Location of this dependency cache
			/// </summary>
			public FileReference Location;

			/// <summary>
			/// Directory for files to cache dependencies for.
			/// </summary>
			public DirectoryReference BaseDir;

			/// <summary>
			/// Map from file item to dependency info
			/// </summary>
			public ConcurrentDictionary<FileItem, DependencyInfo> DependencyFileToInfo = new ConcurrentDictionary<FileItem, DependencyInfo>();

			/// <summary>
			/// Whether the cache has been modified and needs to be saved
			/// </summary>
			public bool bModified;

			/// <summary>
			/// Constructs a dependency cache. This method is private; call CppDependencyCache.Create() to create a cache hierarchy for a given project.
			/// </summary>
			/// <param name="Location">File to store the cache</param>
			/// <param name="BaseDir">Base directory for files that this cache should store data for</param>
			public CachePartition(FileReference Location, DirectoryReference BaseDir)
			{
				this.Location = Location;
				this.BaseDir = BaseDir;

				if (FileReference.Exists(Location))
				{
					Read();
				}
			}

			/// <summary>
			/// Reads data for this dependency cache from disk
			/// </summary>
			public void Read()
			{
				try
				{
					using (BinaryArchiveReader Reader = new BinaryArchiveReader(Location))
					{
						int Version = Reader.ReadInt();
						if (Version != CurrentVersion)
						{
							Log.TraceLog("Unable to read dependency cache from {0}; version {1} vs current {2}", Location, Version, CurrentVersion);
							return;
						}

						int Count = Reader.ReadInt();
						for (int Idx = 0; Idx < Count; Idx++)
						{
							FileItem File = Reader.ReadFileItem()!;
							DependencyFileToInfo[File] = DependencyInfo.Read(Reader);
						}
					}
				}
				catch (Exception Ex)
				{
					Log.TraceWarning("Unable to read {0}. See log for additional information.", Location);
					Log.TraceLog("{0}", ExceptionUtils.FormatExceptionDetails(Ex));
				}
			}

			/// <summary>
			/// Writes data for this dependency cache to disk
			/// </summary>
			public void Write()
			{
				DirectoryReference.CreateDirectory(Location.Directory);
				using (FileStream Stream = File.Open(Location.FullName, FileMode.Create, FileAccess.Write, FileShare.Read))
				{
					using (BinaryArchiveWriter Writer = new BinaryArchiveWriter(Stream))
					{
						Writer.WriteInt(CurrentVersion);

						Writer.WriteInt(DependencyFileToInfo.Count);
						foreach (KeyValuePair<FileItem, DependencyInfo> Pair in DependencyFileToInfo)
						{
							Writer.WriteFileItem(Pair.Key);
							Pair.Value.Write(Writer);
						}
					}
				}
				bModified = false;
			}
		}

		/// <summary>
		/// List of partitions
		/// </summary>
		List<CachePartition> Partitions = new List<CachePartition>();

		/// <summary>
		/// Static cache of all constructed dependency caches
		/// </summary>
		static Dictionary<FileReference, CachePartition> GlobalPartitions = new Dictionary<FileReference, CachePartition>();

		/// <summary>
		/// Constructs a dependency cache. This method is private; call CppDependencyCache.Create() to create a cache hierarchy for a given project.
		/// </summary>
		public CppDependencyCache()
		{
		}

		/// <summary>
		/// Gets the produced module from a dependencies file
		/// </summary>
		/// <param name="InputFile">The dependencies file</param>
		/// <param name="OutModule">The produced module name</param>
		/// <returns>True if a produced module was found</returns>
		public bool TryGetProducedModule(FileItem InputFile, [NotNullWhen(true)] out string? OutModule)
		{
			DependencyInfo? Info;
			if (TryGetDependencyInfo(InputFile, out Info) && Info.ProducedModule != null)
			{
				OutModule = Info.ProducedModule;
				return true;
			}
			else
			{
				OutModule = null;
				return false;
			}
		}

		/// <summary>
		/// Attempts to get a list of imported modules for the given file
		/// </summary>
		/// <param name="InputFile">The dependency file to query</param>
		/// <param name="OutImportedModules">List of imported modules</param>
		/// <returns>True if a list of imported modules was obtained</returns>
		public bool TryGetImportedModules(FileItem InputFile, [NotNullWhen(true)] out List<(string Name, string BMI)>? OutImportedModules)
		{
			DependencyInfo? Info;
			if (TryGetDependencyInfo(InputFile, out Info))
			{
				OutImportedModules = Info.ImportedModules;
				return OutImportedModules != null;
			}
			else
			{
				OutImportedModules = null;
				return false;
			}
		}

		/// <summary>
		/// Attempts to read the dependencies from the given input file
		/// </summary>
		/// <param name="InputFile">File to be read</param>
		/// <param name="OutDependencyItems">Receives a list of output items</param>
		/// <returns>True if the input file exists and the dependencies were read</returns>
		public bool TryGetDependencies(FileItem InputFile, [NotNullWhen(true)] out List<FileItem>? OutDependencyItems)
		{
			DependencyInfo? Info;
			if (TryGetDependencyInfo(InputFile, out Info))
			{
				OutDependencyItems = Info.Files;
				return true;
			}
			else
			{
				OutDependencyItems = null;
				return false;
			}
		}

		/// <summary>
		/// Attempts to read the dependencies from the given input file
		/// </summary>
		/// <param name="InputFile">File to be read</param>
		/// <param name="OutInfo">The dependency info</param>
		/// <returns>True if the input file exists and the dependencies were read</returns>
		private bool TryGetDependencyInfo(FileItem InputFile, [NotNullWhen(true)] out DependencyInfo? OutInfo)
		{
			if (!InputFile.Exists)
			{
				OutInfo = null;
				return false;
			}

			try
			{
				return TryGetDependencyInfoInternal(InputFile, out OutInfo);
			}
			catch (BuildException Ex)
			{
				throw Ex;
			}
			catch (Exception Ex)
			{
				Log.TraceLog("Unable to read {0}:\n{1}", InputFile, ExceptionUtils.FormatExceptionDetails(Ex));
				OutInfo = null;
				return false;
			}
		}

		/// <summary>
		/// Attempts to read dependencies from the given file.
		/// </summary>
		/// <param name="InputFile">File to be read</param>
		/// <param name="OutInfo">The dependency info</param>
		/// <returns>True if the input file exists and the dependencies were read</returns>
		private bool TryGetDependencyInfoInternal(FileItem InputFile, [NotNullWhen(true)] out DependencyInfo? OutInfo)
		{
			foreach(CachePartition Partition in Partitions)
			{
				if (InputFile.Location.IsUnderDirectory(Partition.BaseDir))
				{
					DependencyInfo? Info;
					if (!Partition.DependencyFileToInfo.TryGetValue(InputFile, out Info) || InputFile.LastWriteTimeUtc.Ticks > Info.LastWriteTimeUtc)
					{
						Info = ReadDependencyInfo(InputFile);
						Partition.DependencyFileToInfo.TryAdd(InputFile, Info);
						Partition.bModified = true;
					}

					OutInfo = Info;
					return true;
				}
			}

			OutInfo = null;
			return false;
		}

		/// <summary>
		/// Creates a cache hierarchy for a particular target
		/// </summary>
		/// <param name="ProjectFile">Project file for the target being built</param>
		/// <param name="TargetName">Name of the target</param>
		/// <param name="Platform">Platform being built</param>
		/// <param name="Configuration">Configuration being built</param>
		/// <param name="TargetType">The target type</param>
		/// <param name="Architecture">The target architecture</param>
		/// <returns>Dependency cache hierarchy for the given project</returns>
		public void Mount(FileReference? ProjectFile, string TargetName, UnrealTargetPlatform Platform, UnrealTargetConfiguration Configuration, TargetType TargetType, string Architecture)
		{
			if (ProjectFile == null || !Unreal.IsEngineInstalled())
			{
				string AppName;
				if (TargetType == TargetType.Program)
				{
					AppName = TargetName;
				}
				else
				{
					AppName = UEBuildTarget.GetAppNameForTargetType(TargetType);
				}

				FileReference EngineCacheLocation = FileReference.Combine(Unreal.EngineDirectory, UEBuildTarget.GetPlatformIntermediateFolder(Platform, Architecture, false), AppName, Configuration.ToString(), "DependencyCache.bin");
				FindOrAddPartition(EngineCacheLocation, Unreal.EngineDirectory);
			}

			if (ProjectFile != null)
			{
				FileReference ProjectCacheLocation = FileReference.Combine(ProjectFile.Directory, UEBuildTarget.GetPlatformIntermediateFolder(Platform, Architecture, false), TargetName, Configuration.ToString(), "DependencyCache.bin");
				FindOrAddPartition(ProjectCacheLocation, ProjectFile.Directory);
			}
		}

		/// <summary>
		/// Reads a cache from the given location, or creates it with the given settings
		/// </summary>
		/// <param name="Location">File to store the cache</param>
		/// <param name="BaseDir">Base directory for files that this cache should store data for</param>
		/// <returns>Reference to a dependency cache with the given settings</returns>
		void FindOrAddPartition(FileReference Location, DirectoryReference BaseDir)
		{
			lock (GlobalPartitions)
			{
				if (!Partitions.Any(x => x.Location == Location))
				{
					CachePartition? Partition;
					if (!GlobalPartitions.TryGetValue(Location, out Partition))
					{
						Partition = new CachePartition(Location, BaseDir);
						GlobalPartitions.Add(Location, Partition);
					}
					Partitions.Add(Partition);
				}
			}
		}

		/// <summary>
		/// Save all the caches that have been modified
		/// </summary>
		public static void SaveAll()
		{
			Parallel.ForEach(GlobalPartitions.Values, Cache => { if (Cache.bModified) { Cache.Write(); } });
		}


		/// <summary>
		/// Reads dependencies from the given file.
		/// </summary>
		/// <param name="InputFile">The file to read from</param>
		/// <returns>List of included dependencies</returns>
		static DependencyInfo ReadDependencyInfo(FileItem InputFile)
		{
			if (InputFile.HasExtension(".d"))
			{
				string Text = FileReference.ReadAllText(InputFile.Location);

				List<string> Tokens = new List<string>();

				StringBuilder Token = new StringBuilder();
				for (int Idx = 0; TryReadMakefileToken(Text, ref Idx, Token);)
				{
					Tokens.Add(Token.ToString());
				}

				int TokenIdx = 0;
				while (TokenIdx < Tokens.Count && Tokens[TokenIdx] == "\n")
				{
					TokenIdx++;
				}

				if (TokenIdx + 1 >= Tokens.Count || Tokens[TokenIdx + 1] != ":")
				{
					throw new BuildException($"Unable to parse dependency file {InputFile.Location}");
				}

				TokenIdx += 2;

				List<FileItem> NewDependencyFiles = new List<FileItem>();
				for (; TokenIdx < Tokens.Count && Tokens[TokenIdx] != "\n"; TokenIdx++)
				{
					NewDependencyFiles.Add(FileItem.GetItemByPath(Tokens[TokenIdx]));
				}

				while (TokenIdx < Tokens.Count && Tokens[TokenIdx] == "\n")
				{
					TokenIdx++;
				}

				if (TokenIdx != Tokens.Count)
				{
					throw new BuildException($"Unable to parse dependency file {InputFile.Location}");
				}

				return new DependencyInfo(InputFile.LastWriteTimeUtc.Ticks, null, null, NewDependencyFiles);
			}
			else if (InputFile.HasExtension(".txt"))
			{
				string[] Lines = FileReference.ReadAllLines(InputFile.Location);

				HashSet<FileItem> DependencyItems = new HashSet<FileItem>();
				foreach (string Line in Lines)
				{
					if (Line.Length > 0)
					{
						// Ignore *.tlh and *.tli files generated by the compiler from COM DLLs
						if (!Line.EndsWith(".tlh", StringComparison.OrdinalIgnoreCase) && !Line.EndsWith(".tli", StringComparison.OrdinalIgnoreCase))
						{
							string FixedLine = Line.Replace("\\\\", "\\"); // ISPC outputs files with escaped slashes
							DependencyItems.Add(FileItem.GetItemByPath(FixedLine));
						}
					}
				}
				return new DependencyInfo(InputFile.LastWriteTimeUtc.Ticks, null, null, DependencyItems.ToList());
			}
			else if (InputFile.HasExtension(".json"))
			{
				// https://docs.microsoft.com/en-us/cpp/build/reference/sourcedependencies?view=msvc-160&viewFallbackFrom=vs-2019

				JsonObject Object = JsonObject.Read(InputFile.Location);

				if (!Object.TryGetStringField("Version", out string? Version))
				{
					throw new BuildException(
						$"Dependency file \"{InputFile.Location}\" does not have have a \"Version\" field.");
				}

				if (!String.Equals(Version, "1.1") &&
					!String.Equals(Version, "1.0"))
				{
					throw new BuildException(
						$"Dependency file \"{InputFile.Location}\" version (\"{Version}\") is not supported version");
				}

				JsonObject? Data;
				if (!Object.TryGetObjectField("Data", out Data))
				{
					throw new BuildException("Missing 'Data' field in {0}", InputFile);
				}

				Data.TryGetStringField("ProvidedModule", out string? ProducedModule);

				List<(string Name, string BMI)>? ImportedModules = null;

				if (String.Equals(Version, "1.1") && !InputFile.HasExtension(".md.json"))
				{
					if (Data.TryGetObjectArrayField("ImportedModules", out JsonObject[]? ImportedModulesJson))
					{
						if (ImportedModulesJson.Count() > 0)
						{
							ImportedModules = new List<(string Name, string BMI)>();

							foreach (JsonObject ImportedModule in ImportedModulesJson)
							{
								ImportedModule.TryGetStringField("Name", out string? Name);
								ImportedModule.TryGetStringField("BMI", out string? BMI);

								ImportedModules.Add((Name!, BMI!));
							}
						}
					}
				}
				else
				{ 
					if (Data.TryGetStringArrayField("ImportedModules", out string[]? ImportedModuleArray) && ImportedModuleArray.Length > 0)
					{
						ImportedModules =
							new List<(string Name, string BMI)>(ImportedModuleArray.ConvertAll(x => (x, "")));
					}
				}

				List<FileItem> Files = new List<FileItem>();
				{
					Data.TryGetStringArrayField("Includes", out string[]? Includes);

					if (Includes != null)
					{
						foreach (string Include in Includes)
						{
							Files.Add(FileItem.GetItemByPath(Include));
						}
					}
				}

				return new DependencyInfo(InputFile.LastWriteTimeUtc.Ticks, ProducedModule, ImportedModules, Files);
			}
			else
			{
				throw new BuildException("Unknown dependency list file type: {0}", InputFile);
			}
		}

		/// <summary>
		/// Attempts to read a single token from a makefile
		/// </summary>
		/// <param name="Text">Text to read from</param>
		/// <param name="RefIdx">Current position within the file</param>
		/// <param name="Token">Receives the token characters</param>
		/// <returns>True if a token was read, false if the end of the buffer was reached</returns>
		static bool TryReadMakefileToken(string Text, ref int RefIdx, StringBuilder Token)
		{
			Token.Clear();

			int Idx = RefIdx;
			for (; ; )
			{
				if (Idx == Text.Length)
				{
					return false;
				}

				// Skip whitespace
				while (Text[Idx] == ' ' || Text[Idx] == '\t')
				{
					if (++Idx == Text.Length)
					{
						return false;
					}
				}

				// Colon token
				if (Text[Idx] == ':')
				{
					Token.Append(':');
					RefIdx = Idx + 1;
					return true;
				}

				// Check for a newline
				if (Text[Idx] == '\r' || Text[Idx] == '\n')
				{
					Token.Append('\n');
					RefIdx = Idx + 1;
					return true;
				}

				// Check for an escaped newline
				if (Text[Idx] == '\\' && Idx + 1 < Text.Length)
				{
					if (Text[Idx + 1] == '\n')
					{
						Idx += 2;
						continue;
					}
					if (Text[Idx + 1] == '\r' && Idx + 2 < Text.Length && Text[Idx + 2] == '\n')
					{
						Idx += 3;
						continue;
					}
				}

				// Read a token. Special handling for drive letters on Windows!
				for (; Idx < Text.Length; Idx++)
				{
					if (Text[Idx] == ' ' || Text[Idx] == '\t' || Text[Idx] == '\r' || Text[Idx] == '\n')
					{
						break;
					}
					if (Text[Idx] == ':' && Token.Length > 1)
					{
						break;
					}
					if (Text[Idx] == '\\' && Idx + 1 < Text.Length && Text[Idx + 1] == ' ')
					{
						Idx++;
					}
					Token.Append(Text[Idx]);
				}

				RefIdx = Idx;
				return true;
			}
		}
	}
}
