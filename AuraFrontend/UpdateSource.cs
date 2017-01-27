using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace AuraFrontend
{
	class UpdateSource
	{
		private readonly string _repoDir;
		private readonly string _gitClonePath;

		public UpdateSource(string repoDir, string gitClonePath)
		{
			_repoDir = repoDir;
			_gitClonePath = gitClonePath;
		}

		public bool Update()
		{
			bool created;
			var repo = GetGitRepo(out created);
			var updated = UpdateGit(repo);

			return created || updated;
		}

		Repository GetGitRepo(out bool created)
		{
			created = false;

			using (var t = new ChangingOutput("Reading Git repository . . ."))
			{
				if (Repository.IsValid(_repoDir))
				{
					t.PrintResult(true);
					return new Repository(_repoDir);
				}

				t.PrintResult(false);
			}

			using (var t = new ChangingOutput("Repository not valid - redownloading Aura source . . ."))
			{
				try
				{
					Repository.Clone(_gitClonePath, _repoDir, new CloneOptions()
					{
						OnTransferProgress = (x) =>
						{
							t.PrintProgress((double)x.ReceivedObjects / x.TotalObjects);
							return true;
						}
					});
				}
				catch
				{
					t.PrintResult(false);
					throw;
				}

				t.PrintResult(true);

				created = true;
			}

			return new Repository(_repoDir);
		}

		bool RestoreDeleteFiles(IRepository repo)
		{
			var toExamine = new Stack<TreeEntry>(repo.Head.Tip.Tree.Where(x => x.TargetType == TreeEntryTargetType.Tree));
			var files = new List<TreeEntry>();
			files.AddRange(repo.Head.Tip.Tree.Where(x => x.TargetType == TreeEntryTargetType.Blob));

			while (toExamine.Count() != 0)
			{
				var next = (Tree)toExamine.Pop().Target;
				files.AddRange(next.Where(x => x.TargetType == TreeEntryTargetType.Blob));
				foreach (var t in next.Where(x => x.TargetType == TreeEntryTargetType.Tree))
					toExamine.Push(t);
			}

			var deleted = files.Select(x => x.Path).Where(x => !File.Exists(Path.Combine(_repoDir, x)));

			if (deleted.Any())
			{
				Console.WriteLine("Restoring deleted files");
				repo.CheckoutPaths(repo.Head.Tip.Sha, deleted,
					new CheckoutOptions
					{
						CheckoutModifiers = CheckoutModifiers.Force,
						CheckoutNotifyFlags = CheckoutNotifyFlags.Dirty,
						OnCheckoutNotify = (s, f) => { Console.WriteLine("Restoring {0}", s); return true; }
					});
				return true;
			}

			return false;
		}

		bool UpdateGit(IRepository repo)
		{
			var recompileNeeded = true;
			using (var _ = new ChangingOutput("Updating source code . . ."))
			{
				_.FinishLine();

				// Update origin URL and re-initialize repo
				var origin = repo.Network.Remotes["origin"];
				if (origin.Url != _gitClonePath)
					repo.Network.Remotes.Update(origin, r => r.Url = _gitClonePath);

				using (var t = new ChangingOutput("Fetching updates from remote . . ."))
				{
					repo.Fetch("origin", new FetchOptions()
					{
						OnTransferProgress = (x) =>
						{
							t.PrintProgress((double)x.ReceivedObjects / x.TotalObjects);
							return true;
						}
					});

					t.PrintResult(true);
				}

				var currentCommit = repo.Head.Tip;
				MergeResult result;
				try
				{
					using (var t = new ChangingOutput("Merging in updates . . ."))
					{
						result = repo.Merge(repo.Branches["origin/master"], new Signature(Environment.UserName, "foo@bar.com", DateTime.Now),
							new MergeOptions
							{
								CommitOnSuccess = true,
								FileConflictStrategy = CheckoutFileConflictStrategy.Ours,
								MergeFileFavor = MergeFileFavor.Normal,
								OnCheckoutProgress = (n, processed, total) =>
								{
									t.PrintProgress((double)processed / total);
								},
							});

						t.PrintResult(result.Status != MergeStatus.Conflicts);

					}

					if (result.Status == MergeStatus.UpToDate)
					{
						Console.WriteLine("Source was already up to date");
						recompileNeeded = RestoreDeleteFiles(repo);
						_.PrintResult(true);
					}
					else if (result.Status == MergeStatus.Conflicts)
					{
						throw new MergeConflictException();
					}
					else
					{
						Console.WriteLine("Updated to {0} : {1}", result.Commit.Sha.Substring(0, 10), result.Commit.MessageShort);
						_.PrintResult(true);
					}
				}
				catch (MergeConflictException)
				{
					Console.WriteLine("Merge resulted in conflicts. This usually indictates a user-edited source");
					Console.WriteLine("Your Aura will NOT be updated until you undo your changes to the files.");
					Console.WriteLine("This is a bad thing, so fix it ASAP.");
					Console.WriteLine("NOTE: If you're trying to make configuration changes, use the \"user\" folders instead.");
					Console.WriteLine("Rolling back merge...");
					repo.Reset(currentCommit);
					recompileNeeded = false;
					_.PrintResult(false);
				}

				return recompileNeeded;
			}
		}
	}
}
