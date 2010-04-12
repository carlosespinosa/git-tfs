using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using GitSharp;
using StructureMap;

namespace Sep.Git.Tfs.Core
{
    public class GitHelpers : IGitHelpers
    {
        private readonly TextWriter realStdout;

        public GitHelpers(TextWriter stdout)
        {
            realStdout = stdout;
        }

        /// <summary>
        /// Runs the given git command, and returns the contents of its STDOUT.
        /// </summary>
        public string Command(params string[] command)
        {
            string retVal = null;
            CommandOutputPipe(stdout => retVal = stdout.ReadToEnd(), command);
            return retVal;
        }

        /// <summary>
        /// Runs the given git command, and returns the first line of its STDOUT.
        /// </summary>
        public string CommandOneline(params string[] command)
        {
            string retVal = null;
            CommandOutputPipe(stdout => retVal = stdout.ReadLine(), command);
            return retVal;
        }

        /// <summary>
        /// Runs the given git command, and passes STDOUT through to the current process's STDOUT.
        /// </summary>
        public void CommandNoisy(params string[] command)
        {
            CommandOutputPipe(stdout => realStdout.Write(stdout.ReadToEnd()), command);
        }

        /// <summary>
        /// Runs the given git command, and redirects STDOUT to the provided action.
        /// </summary>
        public void CommandOutputPipe(Action<TextReader> handleOutput, params string[] command)
        {
            Time(command, () =>
                              {
                                  AssertValidCommand(command);
                                  var process = Start(command, RedirectStdout);
                                  handleOutput(process.StandardOutput);
                                  Close(process);
                              });
        }

        /// <summary>
        /// Runs the given git command, and returns a reader for STDOUT. NOTE: The returned value MUST be disposed!
        /// </summary>
        public TextReader CommandOutputPipe(params string[] command)
        {
            AssertValidCommand(command);
            var process = Start(command, RedirectStdout);
            return new ProcessStdoutReader(this, process);
        }

        public class ProcessStdoutReader : TextReader
        {
            private readonly Process process;
            private readonly GitHelpers helper;

            public ProcessStdoutReader(GitHelpers helper, Process process)
            {
                this.helper = helper;
                this.process = process;
            }

            public override void Close()
            {
                process.StandardOutput.Close();
                helper.Close(process);
            }

            public override System.Runtime.Remoting.ObjRef CreateObjRef(Type requestedType)
            {
                return process.StandardOutput.CreateObjRef(requestedType);
            }

            protected override void Dispose(bool disposing)
            {
                if(disposing && process != null)
                {
                    Close();
                }
                base.Dispose(disposing);
            }

            public override bool Equals(object obj)
            {
                return process.StandardOutput.Equals(obj);
            }

            public override int GetHashCode()
            {
                return process.StandardOutput.GetHashCode();
            }

            public override object InitializeLifetimeService()
            {
                return process.StandardOutput.InitializeLifetimeService();
            }

            public override int Peek()
            {
                return process.StandardOutput.Peek();
            }

            public override int Read()
            {
                return process.StandardOutput.Read();
            }

            public override int Read(char[] buffer, int index, int count)
            {
                return process.StandardOutput.Read(buffer, index, count);
            }

            public override int ReadBlock(char[] buffer, int index, int count)
            {
                return process.StandardOutput.ReadBlock(buffer, index, count);
            }

            public override string ReadLine()
            {
                return process.StandardOutput.ReadLine();
            }

            public override string ReadToEnd()
            {
                return process.StandardOutput.ReadToEnd();
            }

            public override string ToString()
            {
                return process.StandardOutput.ToString();
            }
        }

        public void CommandInputPipe(Action<TextWriter> action, params string[] command)
        {
            Time(command, () =>
                              {
                                  AssertValidCommand(command);
                                  var process = Start(command, RedirectStdin);
                                  action(process.StandardInput);
                                  Close(process);
                              });
        }

        public void CommandInputOutputPipe(Action<TextWriter, TextReader> interact, params string[] command)
        {
            Time(command, () =>
                              {
                                  AssertValidCommand(command);
                                  var process = Start(command,
                                                      Ext.And<ProcessStartInfo>(RedirectStdin, RedirectStdout));
                                  interact(process.StandardInput, process.StandardOutput);
                                  Close(process);
                              });
        }

        private void Time(string[] command, Action action)
        {
            var start = DateTime.Now;
            try
            {
                action();
            }
            finally
            {
                var end = DateTime.Now;
                Trace.WriteLine(String.Format("[{0}] {1}", end - start, String.Join(" ", command)), "git command time");
            }
        }

        private void Close(Process process)
        {
            NumberOfProcessesRun++;
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
                throw new GitCommandException("Command did not terminate.", process);
            if(process.ExitCode != 0)
                throw new GitCommandException("Command exited with error code.", process);
        }

        private void RedirectStdout(ProcessStartInfo startInfo)
        {
            startInfo.RedirectStandardOutput = true;
        }

        private void RedirectStdin(ProcessStartInfo startInfo)
        {
            startInfo.RedirectStandardInput = true;
        }

        private Process Start(string[] command)
        {
            return Start(command, x => {});
        }

        protected virtual Process Start(string [] command, Action<ProcessStartInfo> initialize)
        {
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = "git";
            startInfo.SetArguments(command);
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardError = true;
            initialize(startInfo);
            Trace.WriteLine("Starting process: " + startInfo.FileName + " " + startInfo.Arguments, "git command");
            Trace.WriteLine("  Working directory: " + startInfo.WorkingDirectory);
            Trace.WriteLine("  ENV[GIT_DIR]:      " + startInfo.EnvironmentVariables["GIT_DIR"]);
            var process = Process.Start(startInfo);
            process.ErrorDataReceived += StdErrReceived;
            process.BeginErrorReadLine();
            return process;
        }

        private void StdErrReceived(object sender, DataReceivedEventArgs e)
        {
            if(e.Data != null && e.Data.Trim() != "")
            {
                Trace.WriteLine(e.Data.TrimEnd(), "git stderr");
            }
        }

        /// <summary>
        /// WrapGitCommandErrors the actions, and if there are any git exceptions, rethrow a new exception with the given message.
        /// </summary>
        /// <param name="exceptionMessage">A friendlier message to wrap the GitCommandException with. {0} is replaced with the command line and {1} is replaced with the exit code.</param>
        /// <param name="action"></param>
        public void WrapGitCommandErrors(string exceptionMessage, Action action)
        {
            try
            {
                action();
            }
            catch (GitCommandException e)
            {
                throw new Exception(String.Format(exceptionMessage, e.Process.StartInfo.FileName + " " + e.Process.StartInfo.Arguments, e.Process.ExitCode), e);
            }
        }

        public IGitRepository MakeRepository(string dir)
        {
            return MakeRepository(new Repository(dir));
        }

        public IGitRepository MakeRepository(Repository repository)
        {
            return ObjectFactory
                .With("repository").EqualTo(repository)
                .GetInstance<IGitRepository>();
        }

        private static readonly Regex ValidCommandName = new Regex("^[a-z0-9A-Z_-]+$");
        public static int NumberOfProcessesRun;

        private static void AssertValidCommand(string[] command)
        {
            if(command.Length < 1 || !ValidCommandName.IsMatch(command[0]))
                throw new Exception("bad command: " + (command.Length == 0 ? "" : command[0]));
        }
    }
}
