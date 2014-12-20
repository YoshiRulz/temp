﻿using System.Collections.Generic;

namespace BizHawk.Emulation.Common
{
	public interface IDebuggable : IEmulatorService
	{
		/// <summary>
		/// Returns a list of Cpu registers and their current state
		/// </summary>
		/// <returns></returns>
		IDictionary<string, RegisterValue> GetCpuFlagsAndRegisters();

		/// <summary>
		/// Sets a given Cpu register to the given value
		/// </summary>
		/// <param name="register"></param>
		/// <param name="value"></param>
		void SetCpuRegister(string register, int value);

		ITracer Tracer { get; }

		IMemoryCallbackSystem MemoryCallbacks { get; }

		// Advanced Navigation
		//void StepInto();
		//void StepOut();
		//void StepOver();

		void Step(StepType type);
	}

	public class RegisterValue
	{
		public ulong Value { get; set; }
		public byte BitSize { get; set; }

		public static implicit operator RegisterValue(bool val)
		{
			return new RegisterValue
			{
				Value = (ulong)(val ? 1 : 0),
				BitSize = 1
			};
		}

		public static implicit operator RegisterValue(byte val)
		{
			return new RegisterValue
			{
				Value = val,
				BitSize = 8
			};
		}

		public static implicit operator RegisterValue(ushort val)
		{
			return new RegisterValue
			{
				Value = val,
				BitSize = 16
			};
		}

		public static implicit operator RegisterValue(int val)
		{
			return new RegisterValue
			{
				Value = (ulong)val,
				BitSize = 32
			};
		}

		public static implicit operator RegisterValue(uint val)
		{
			return new RegisterValue
			{
				Value = val,
				BitSize = 32
			};
		}

		public static implicit operator RegisterValue(long val)
		{
			return new RegisterValue
			{
				Value = (ulong)val,
				BitSize = 64
			};
		}

		public static implicit operator RegisterValue(ulong val)
		{
			return new RegisterValue
			{
				Value = val,
				BitSize = 64
			};
		}
	}
}
