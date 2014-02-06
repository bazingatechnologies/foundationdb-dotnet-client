﻿#region BSD Licence
/* Copyright (c) 2013, Doxense SARL
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
	* Redistributions of source code must retain the above copyright
	  notice, this list of conditions and the following disclaimer.
	* Redistributions in binary form must reproduce the above copyright
	  notice, this list of conditions and the following disclaimer in the
	  documentation and/or other materials provided with the distribution.
	* Neither the name of Doxense nor the
	  names of its contributors may be used to endorse or promote products
	  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
#endregion

namespace FoundationDB.Filters.Logging
{
	using FoundationDB.Client;
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Globalization;
	using System.Text;
	using System.Threading;

	public sealed partial class FdbTransactionLog
	{
		private int m_step;

		private int m_operations;
		private int m_readSize;
		private int m_writeSize;

		public FdbTransactionLog(IFdbTransaction trans)
		{
			this.Commands = new ConcurrentQueue<Command>();
		}

		/// <summary>Id of the logged transaction</summary>
		public int Id { get; private set; }

		/// <summary>Number of operations performed by the transaction</summary>
		public int Operations { get { return m_operations; } }

		/// <summary>List of all commands processed by the transaction</summary>
		public ConcurrentQueue<Command> Commands { get; private set; }

		/// <summary>Timestamp of the start of transaction</summary>
		public long StartTimestamp { get; private set; }
		/// <summary>Timestamp of the end of transaction</summary>
		public long StopTimestamp { get; private set; }

		/// <summary>Timestamp (UTC) of the start of transaction</summary>
		public DateTimeOffset StartedUtc { get; internal set; }

		/// <summary>Tmiestamp (UTC) of the end of the transaction</summary>
		public DateTimeOffset? StoppedUtc { get; internal set; }

		/// <summary>Timestamp (UTC) of the last successfull commit of the transaction</summary>
		public DateTimeOffset? CommittedUtc { get; internal set; }

		/// <summary>Committed version of the transaction (if a commit was successfull)</summary>
		public long? CommittedVersion { get; private set; }

		/// <summary>Internal step counter of the transaction</summary>
		/// <remarks>This counter is used to detect sequential vs parallel commands</remarks>
		public int Step { get { return m_step; } }

		/// <summary>Read size of the last commit attempt</summary>
		/// <remarks>This value only account for read commands in the last attempt</remarks>
		public int ReadSize { get { return m_readSize; } }

		/// <summary>Write size of the last commit attempt</summary>
		/// <remarks>This value only account for write commands in the last attempt</remarks>
		public int WriteSize { get { return m_writeSize; } }

		/// <summary>Commit size of the last commit attempt</summary>
		/// <remarks>This value only account for write commands in the last attempt</remarks>
		public int CommitSize { get; internal set; }

		/// <summary>Total of the commit size of all attempts performed by this transaction</summary>
		/// <remarks>This value include the size of all previous retry attempts</remarks>
		public int TotalCommitSize { get; internal set; }

		/// <summary>If true, the transaction has completed (either Commit() completed successfully or Dispose was called)</summary>
		public bool Completed { get; private set; }

		/// <summary>Total number of attempts to commit this transaction</summary>
		/// <remarks>This value is increment on each call to Commit()</remarks>
		public int Attempts { get; internal set; }

		private static readonly double R = 1.0d * TimeSpan.TicksPerMillisecond / Stopwatch.Frequency;

		internal static long GetTimestamp()
		{
			return Stopwatch.GetTimestamp();
		}

		internal TimeSpan GetTimeOffset()
		{
			return GetDuration(GetTimestamp() - this.StartTimestamp);
		}

		internal static TimeSpan GetDuration(long elapsed)
		{
			return TimeSpan.FromTicks((long)Math.Round(((double)elapsed / Stopwatch.Frequency) * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
		}

		public TimeSpan TotalDuration
		{
			get
			{
				if (this.StopTimestamp == 0)
					return GetTimeOffset();
				else
					return GetDuration(this.StopTimestamp - this.StartTimestamp);
			}
		}

		public void Start(IFdbTransaction trans)
		{
			this.Id = trans.Id;
			this.StartedUtc = DateTimeOffset.UtcNow;
			this.StartTimestamp = GetTimestamp();
		}

		public void Stop(IFdbTransaction trans)
		{
			if (!this.Completed)
			{
				this.Completed = true;
				this.StopTimestamp = GetTimestamp();
				this.StoppedUtc = DateTimeOffset.UtcNow;
			}
		}

		public void AddOperation(Command cmd, bool countAsOperation = true)
		{
			var ts = GetTimeOffset();
			int step = Volatile.Read(ref m_step);

			cmd.StartOffset = ts;
			cmd.Step = step;
			cmd.EndOffset = cmd.StartOffset;
			cmd.ThreadId = Thread.CurrentThread.ManagedThreadId;
			if (countAsOperation) Interlocked.Increment(ref m_operations);
			this.Commands.Enqueue(cmd);
		}

		public void BeginOperation(Command cmd)
		{
			var ts = GetTimeOffset();
			int step = Volatile.Read(ref m_step);

			cmd.StartOffset = ts;
			cmd.Step = step;
			cmd.ThreadId = Thread.CurrentThread.ManagedThreadId;
			if (cmd.ArgumentBytes.HasValue) Interlocked.Add(ref m_writeSize, cmd.ArgumentBytes.Value);
			Interlocked.Increment(ref m_operations);
			this.Commands.Enqueue(cmd);
		}

		public void EndOperation(Command cmd, Exception error = null)
		{
			var ts = GetTimeOffset();
			var step = Interlocked.Increment(ref m_step);

			cmd.EndOffset = ts;
			cmd.EndStep = step;
			cmd.Error = error;
			if (cmd.ResultBytes.HasValue) Interlocked.Add(ref m_readSize, cmd.ResultBytes.Value);
		}

		public string GetCommandsReport()
		{
			var sb = new StringBuilder();
			sb.AppendLine("Transaction #" + this.Id.ToString() + " command log:");
			int reads = 0, writes = 0;
			var cmds = this.Commands.ToArray();
			for (int i = 0; i < cmds.Length; i++)
			{
				var cmd = cmds[i];
				sb.AppendFormat("{0,3}/{1,3} : {2}", i + 1, cmds.Length, cmd.ToString());
				sb.AppendLine();
				switch (cmd.Mode)
				{
					case FdbTransactionLog.Mode.Read: ++reads; break;
					case FdbTransactionLog.Mode.Write: ++writes; break;
				}
			}
			sb.AppendLine("Stats: " + this.Operations + " operations (" + reads + " reads, " + writes + " writes), " + this.CommitSize + " committed bytes");
			sb.AppendLine();
			return sb.ToString();
		}

		public string GetTimingsReport(bool showCommands = false)
		{
			var sb = new StringBuilder();
			TimeSpan duration = this.TotalDuration;
			// ideal range is between 10 and 80 chars
			double scale = 0.0005d;
			int width;
			bool flag = false;
			while ((width = (int)(duration.TotalSeconds / scale)) > 80)
			{
				if (flag) scale *= 5d; else scale *= 2d;
				flag = !flag;
			}

			var cmds = this.Commands.ToArray();

			// Header
			sb.AppendFormat(CultureInfo.InvariantCulture, "Transaction #{0} ({1} operations, '#' = " + (scale * 1000d).ToString("N1") + " ms, started {2}Z", this.Id, cmds.Length, this.StartedUtc.TimeOfDay);
			if (this.StoppedUtc.HasValue)
				sb.AppendFormat(CultureInfo.InvariantCulture, ", ended {0}Z)", this.StoppedUtc.Value.TimeOfDay); 
			else
				sb.AppendLine(", did not finish");
			sb.AppendLine();
			sb.AppendLine("┌  oper. ┬" + new string('─', width + 2) + "┬──── start ──── end ── duration ──┬─ sent  recv ┐");

			int step = -1;
			bool previousWasOnError = false;
			int attempts = 1;
			int charsToSkip = 0;
			foreach (var cmd in cmds)
			{
				if (previousWasOnError)
				{ // │
					sb.AppendLine("├────────┼" + new string('─', 2 + width) + "┼──────────────────────────────────┼─────────────┤ == Attempt #" + (++attempts).ToString() + " ==");
				}

				long ticks = cmd.Duration.Ticks;
				double r = 1.0d * ticks / duration.Ticks;
				string w = GetFancyGraph(width, cmd.StartOffset.Ticks, ticks, duration.Ticks, charsToSkip);

				sb.AppendFormat(
					CultureInfo.InvariantCulture, 
					"│{6}{1,-3:##0}{10}{0,2}{7}│ {2} │ T+{3,7:##0.000} ~ {4,7:##0.000} ({5,7:##,##0} µs) │ {8,5} {9,5} │ {11}",
					/* 0 */ cmd.ShortName,
					/* 1 */ cmd.Step,
					/* 2 */ w,
					/* 3 */ cmd.StartOffset.TotalMilliseconds,
					/* 4 */ (cmd.EndOffset ?? TimeSpan.Zero).TotalMilliseconds,
					/* 5 */ ticks / 10.0,
					/* 6 */ cmd.Step == step ? ":" : " ",
					/* 7 */ ticks >= 100000 ? "*" : ticks >= 10000 ? "°" : " ",
					/* 8 */ cmd.ArgumentBytes,
					/* 9 */ cmd.ResultBytes,
					/* 10 */ cmd.Error != null ? "!" : " ",
					/* 11 */ showCommands ? cmd.ToString() : String.Empty
				);
				sb.AppendLine();

				previousWasOnError = cmd.Op == Operation.OnError;
				if (previousWasOnError)
				{
					charsToSkip = (int)Math.Floor(1.0d * width * (cmd.EndOffset ?? TimeSpan.Zero).Ticks / duration.Ticks);
				}

				step = cmd.Step;
			}

			sb.AppendLine("└────────┴" + new string('─', width + 2) + "┴──────────────────────────────────┴─────────────┘");

			// Footer
			if (this.Completed)
			{
				flag = false;
				if (this.ReadSize > 0)
				{
					sb.Append("Read " + this.ReadSize.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
					flag = true;
				}
				if (this.CommitSize > 0)
				{
					if (flag) sb.Append(" and ");
					sb.Append("Committed " + this.CommitSize.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
					flag = true;
				}
				if (!flag) sb.Append("Completed");
				sb.AppendLine(" in " + duration.TotalMilliseconds.ToString("N3", CultureInfo.InvariantCulture) + " ms and " + attempts.ToString(CultureInfo.InvariantCulture) + " attempt(s)");
			}
			return sb.ToString();
		}

		private static char GetFancyChar(int pos, int count, double start, double end, bool skip)
		{
			double cb = 1.0 * pos / count;
			double ce = 1.0 * (pos + 1) / count;

			if (cb >= end) return ' ';
			if (ce < start) return skip ? '°' : '_';

			double x = count * (Math.Min(ce, end) - Math.Max(cb, start));
			if (x < 0) x = 0;
			if (x > 1) x = 1;

			int p = (int)Math.Round(x * 10);
			return "`.:;+=xX$&#"[p];
		}

		private static string GetFancyGraph(int width, long offset, long duration, long total, int skip)
		{
			double begin = 1.0d * offset / total;
			double end = 1.0d * (offset + duration) / total;

			var tmp = new char[width];
			for (int i = 0; i < tmp.Length; i++)
			{
				tmp[i] = GetFancyChar(i, tmp.Length, begin, end, i < skip);
			}
			return new string(tmp);
		}

		public enum Operation
		{
			Invalid = 0,

			Set,
			Clear,
			ClearRange,
			Atomic,
			AddConflictRange,
			Get,
			GetKey,
			GetValues,
			GetKeys,
			GetRange,
			Watch,

			GetReadVersion,
			Commit,
			Cancel,
			Reset,
			OnError,

			Log,
		}

		public enum Mode
		{
			Invalid = 0,
			Read,
			Write,
			Meta,
			Watch,
			Annotation
		}

	}

}
