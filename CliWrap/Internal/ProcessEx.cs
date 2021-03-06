﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CliWrap.Internal
{
    internal class ProcessEx : IDisposable
    {
        private readonly Process _nativeProcess;

        private readonly TaskCompletionSource<object?> _exitTcs =
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _isKilled;

        public int Id { get; private set; }

        // We are purposely using Stream instead of StreamWriter/StreamReader to push the concerns of
        // writing and reading to PipeSource/PipeTarget at the higher level.

        public Stream StdIn { get; private set; } = Stream.Null;

        public Stream StdOut { get; private set; } = Stream.Null;

        public Stream StdErr { get; private set; } = Stream.Null;

        public int ExitCode { get; private set; }

        public DateTimeOffset StartTime { get; private set; }

        public DateTimeOffset ExitTime { get; private set; }

        public ProcessEx(ProcessStartInfo startInfo) =>
            _nativeProcess = new Process {StartInfo = startInfo};

        public void Start()
        {
            Debug.Assert(Id == default, "Attempt to launch a process more than once.");

            // Hook up events
            _nativeProcess.EnableRaisingEvents = true;
            _nativeProcess.Exited += (sender, args) =>
            {
                ExitTime = _nativeProcess.ExitTime;
                ExitCode = _nativeProcess.ExitCode;

                if (!_isKilled)
                {
                    _exitTcs.TrySetResult(null);
                }
                else
                {
                    _exitTcs.TrySetCanceled();
                }
            };

            // Start the process
            if (!_nativeProcess.Start())
            {
                throw new InvalidOperationException(
                    "Failed to obtain the handle when starting a process. " +
                    "This could mean that the target executable doesn't exist or that execute permission is missing."
                );
            }

            // Copy metadata
            Id = _nativeProcess.Id;
            StdIn = _nativeProcess.StandardInput.BaseStream;
            StdOut = _nativeProcess.StandardOutput.BaseStream;
            StdErr = _nativeProcess.StandardError.BaseStream;
            StartTime = _nativeProcess.StartTime;
        }

        public void Kill()
        {
            try
            {
                _isKilled = true;
                _nativeProcess.Kill(true);
            }
            catch
            {
                // All exceptions could indicate that the process has already
                // exited or is currently getting terminated.
                // We can't know if the process has actually failed to terminate,
                // so, as a safeguard, we will cancel the task source after X seconds.
                // Ideally, WaitUntilExitAsync() should NOT return before the process terminates.
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
                {
                    if (_exitTcs.TrySetCanceled())
                        Debug.Fail("Process termination timed out.");
                });
            }
        }

        public async Task WaitUntilExitAsync() => await _exitTcs.Task;

        public void Dispose() => _nativeProcess.Dispose();
    }
}