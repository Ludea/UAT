// Copyright Epic Games, Inc. All Rights Reserved.

using EpicGames.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using AutomationTool;
using UnrealBuildBase;

namespace BuildGraph.Tasks
{
	/// <summary>
	/// Parameters for a Docker-Build task
	/// </summary>
	public class DockerBuildTaskParameters
	{
		/// <summary>
		/// Base directory for the build
		/// </summary>
		[TaskParameter]
		public string BaseDir;

		/// <summary>
		/// Files to be staged before building the image
		/// </summary>
		[TaskParameter]
		public string Files;

		/// <summary>
		/// Path to the Dockerfile. Uses the root of basedir if not specified.
		/// </summary>
		[TaskParameter(Optional = true)]
		public string DockerFile;

		/// <summary>
		/// Tag for the image
		/// </summary>
		[TaskParameter(Optional = true)]
		public string Tag;

		/// <summary>
		/// Optional arguments
		/// </summary>
		[TaskParameter(Optional = true)]
		public string Arguments;

		/// <summary>
		/// List of additional directories to overlay into the staged input files. Allows credentials to be staged, etc...
		/// </summary>
		[TaskParameter(Optional = true)]
		public string OverlayDirs;

		/// <summary>
		/// Environment variables to set
		/// </summary>
		[TaskParameter(Optional = true)]
		public string Environment;

		/// <summary>
		/// File to read environment variables from
		/// </summary>
		[TaskParameter(Optional = true)]
		public string EnvironmentFile;
	}

	/// <summary>
	/// Spawns Docker and waits for it to complete.
	/// </summary>
	[TaskElement("Docker-Build", typeof(DockerBuildTaskParameters))]
	public class DockerBuildTask : SpawnTaskBase
	{
		/// <summary>
		/// Parameters for this task
		/// </summary>
		DockerBuildTaskParameters Parameters;

		/// <summary>
		/// Construct a Docker task
		/// </summary>
		/// <param name="InParameters">Parameters for the task</param>
		public DockerBuildTask(DockerBuildTaskParameters InParameters)
		{
			Parameters = InParameters;
		}

		/// <summary>
		/// Execute the task.
		/// </summary>
		/// <param name="Job">Information about the current job</param>
		/// <param name="BuildProducts">Set of build products produced by this node.</param>
		/// <param name="TagNameToFileSet">Mapping from tag names to the set of files they include</param>
		public override void Execute(JobContext Job, HashSet<FileReference> BuildProducts, Dictionary<string, HashSet<FileReference>> TagNameToFileSet)
		{
			Log.TraceInformation("Building Docker image");
			using (LogIndentScope Scope = new LogIndentScope("  "))
			{
				DirectoryReference BaseDir = ResolveDirectory(Parameters.BaseDir);
				List<FileReference> SourceFiles = ResolveFilespec(BaseDir, Parameters.Files, TagNameToFileSet).ToList();

				DirectoryReference StagingDir = DirectoryReference.Combine(Unreal.EngineDirectory, "Intermediate", "Docker");
				FileUtils.ForceDeleteDirectoryContents(StagingDir);

				List<FileReference> TargetFiles = SourceFiles.ConvertAll(x => FileReference.Combine(StagingDir, x.MakeRelativeTo(BaseDir)));
				CommandUtils.ThreadedCopyFiles(SourceFiles, BaseDir, StagingDir);

				if (!String.IsNullOrEmpty(Parameters.OverlayDirs))
				{
					foreach (string OverlayDir in Parameters.OverlayDirs.Split(';'))
					{
						CommandUtils.ThreadedCopyFiles(ResolveDirectory(OverlayDir), StagingDir);
					}
				}

				StringBuilder Arguments = new StringBuilder("build .");
				if (Parameters.Tag != null)
				{
					Arguments.Append($" -t {Parameters.Tag}");
				}
				if (Parameters.DockerFile != null)
				{
					FileReference DockerFile = ResolveFile(Parameters.DockerFile);
					if (!DockerFile.IsUnderDirectory(BaseDir))
					{
						throw new AutomationException($"Dockerfile '{DockerFile}' is not under base directory ({BaseDir})");
					}
					Arguments.Append($" -f {DockerFile.MakeRelativeTo(BaseDir).QuoteArgument()}");
				}
				if (Parameters.Arguments != null)
				{
					Arguments.Append($" {Parameters.Arguments}");
				}

				SpawnTaskBase.Execute("docker", Arguments.ToString(), EnvVars: ParseEnvVars(Parameters.Environment, Parameters.EnvironmentFile), WorkingDir: StagingDir.FullName);
			}
		}

		/// <summary>
		/// Output this task out to an XML writer.
		/// </summary>
		public override void Write(XmlWriter Writer)
		{
			Write(Writer, Parameters);
		}

		/// <summary>
		/// Find all the tags which are used as inputs to this task
		/// </summary>
		/// <returns>The tag names which are read by this task</returns>
		public override IEnumerable<string> FindConsumedTagNames()
		{
			List<string> TagNames = new List<string>();
			TagNames.AddRange(FindTagNamesFromFilespec(Parameters.DockerFile));
			TagNames.AddRange(FindTagNamesFromFilespec(Parameters.Files));
			return TagNames;
		}

		/// <summary>
		/// Find all the tags which are modified by this task
		/// </summary>
		/// <returns>The tag names which are modified by this task</returns>
		public override IEnumerable<string> FindProducedTagNames()
		{
			yield break;
		}
	}
}
