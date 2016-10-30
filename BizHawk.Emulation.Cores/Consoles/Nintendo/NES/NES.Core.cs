using System;
using System.Linq;

using BizHawk.Common;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Cores.Components.M6502;

#pragma warning disable 162

namespace BizHawk.Emulation.Cores.Nintendo.NES
{
	public partial class NES : IEmulator
	{
		//hardware/state
		public MOS6502X cpu;
		int cpu_accumulate; //cpu timekeeper
		public PPU ppu;
		public APU apu;
		public byte[] ram;
		NESWatch[] sysbus_watch = new NESWatch[65536];
		public byte[] CIRAM; //AKA nametables
		string game_name = string.Empty; //friendly name exposed to user and used as filename base
		CartInfo cart; //the current cart prototype. should be moved into the board, perhaps
		internal INESBoard Board; //the board hardware that is currently driving things
		EDetectionOrigin origin = EDetectionOrigin.None;
		int sprdma_countdown;

		bool _irq_apu; //various irq signals that get merged to the cpu irq pin
					   /// <summary>clock speed of the main cpu in hz</summary>
		public int cpuclockrate { get; private set; }

		//irq state management
		public bool irq_apu { get { return _irq_apu; } set { _irq_apu = value; } }

		//user configuration 
		int[] palette_compiled = new int[64 * 8];

		//variable to change controller read/write behaviour when keyboard is attached
		public bool _iskeyboard = false;
		//variable set when VS system games are running
		internal bool _isVS = false;
		//some VS games have a ppu that switches 2000 and 2001, so keep trcak of that
		public byte _isVS2c05 = 0;
		//since prg reg for VS System is set in the controller regs, it is convenient to have it here
		//instead of in the board
		public byte VS_chr_reg;
		public byte VS_prg_reg;
		//various VS controls
		public byte[] VS_dips = new byte[8];
		public byte VS_service = 0;
		public byte VS_coin_inserted=0;
		public byte VS_ROM_control;

		// new input system
		NESControlSettings ControllerSettings; // this is stored internally so that a new change of settings won't replace
		IControllerDeck ControllerDeck;
		byte latched4016;

		private DisplayType _display_type = DisplayType.NTSC;

		//Sound config
		public void SetSquare1(int v) { apu.Square1V = v; }
		public void SetSquare2(int v) { apu.Square2V = v; }
		public void SetTriangle(int v) { apu.TriangleV = v; }
		public void SetNoise(int v) { apu.NoiseV = v; }
		public void SetDMC(int v) { apu.DMCV = v; }

		/// <summary>
		/// for debugging only!
		/// </summary>
		/// <returns></returns>
		public INESBoard GetBoard()
		{
			return Board;
		}

		public void Dispose()
		{
			if (magicSoundProvider != null)
				magicSoundProvider.Dispose();
			magicSoundProvider = null;
		}

		class MagicSoundProvider : ISoundProvider, ISyncSoundProvider, IDisposable
		{
			BlipBuffer blip;
			NES nes;

			const int blipbuffsize = 4096;

			public MagicSoundProvider(NES nes, uint infreq)
			{
				this.nes = nes;

				blip = new BlipBuffer(blipbuffsize);
				blip.SetRates(infreq, 44100);

				//var actualMetaspu = new Sound.MetaspuSoundProvider(Sound.ESynchMethod.ESynchMethod_V);
				//1.789773mhz NTSC
				//resampler = new Sound.Utilities.SpeexResampler(2, infreq, 44100 * APU.DECIMATIONFACTOR, infreq, 44100, actualMetaspu.buffer.enqueue_samples);
				//output = new Sound.Utilities.DCFilter(actualMetaspu);
			}

			public void GetSamples(short[] samples)
			{
				//Console.WriteLine("Sync: {0}", nes.apu.dlist.Count);
				int nsamp = samples.Length / 2;
				if (nsamp > blipbuffsize) // oh well.
					nsamp = blipbuffsize;
				uint targetclock = (uint)blip.ClocksNeeded(nsamp);
				uint actualclock = nes.apu.sampleclock;
				foreach (var d in nes.apu.dlist)
					blip.AddDelta(d.time * targetclock / actualclock, d.value);
				nes.apu.dlist.Clear();
				blip.EndFrame(targetclock);
				nes.apu.sampleclock = 0;

				blip.ReadSamples(samples, nsamp, true);
				// duplicate to stereo
				for (int i = 0; i < nsamp * 2; i += 2)
					samples[i + 1] = samples[i];

				//mix in the cart's extra sound circuit
				nes.Board.ApplyCustomAudio(samples);
			}

			public void GetSamples(out short[] samples, out int nsamp)
			{
				//Console.WriteLine("ASync: {0}", nes.apu.dlist.Count);
				foreach (var d in nes.apu.dlist)
					blip.AddDelta(d.time, d.value);
				nes.apu.dlist.Clear();
				blip.EndFrame(nes.apu.sampleclock);
				nes.apu.sampleclock = 0;

				nsamp = blip.SamplesAvailable();
				samples = new short[nsamp * 2];

				blip.ReadSamples(samples, nsamp, true);
				// duplicate to stereo
				for (int i = 0; i < nsamp * 2; i += 2)
					samples[i + 1] = samples[i];

				nes.Board.ApplyCustomAudio(samples);
			}

			public void DiscardSamples()
			{
				nes.apu.dlist.Clear();
				nes.apu.sampleclock = 0;
			}

			public int MaxVolume { get; set; }

			public void Dispose()
			{
				if (blip != null)
				{
					blip.Dispose();
					blip = null;
				}
			}
		}
		MagicSoundProvider magicSoundProvider;

		public void HardReset()
		{
			cpu = new MOS6502X();
			cpu.SetCallbacks(ReadMemory, ReadMemory, PeekMemory, WriteMemory);

			cpu.BCD_Enabled = false;
			cpu.OnExecFetch = ExecFetch;
			ppu = new PPU(this);
			ram = new byte[0x800];
			CIRAM = new byte[0x800];

			// wire controllers
			// todo: allow changing this
			ControllerDeck = ControllerSettings.Instantiate(ppu.LightGunCallback);
			// set controller definition first time only
			if (ControllerDefinition == null)
			{
				ControllerDefinition = new ControllerDefinition(ControllerDeck.GetDefinition());
				ControllerDefinition.Name = "NES Controller";
				// controls other than the deck
				ControllerDefinition.BoolButtons.Add("Power");
				ControllerDefinition.BoolButtons.Add("Reset");
				if (Board is FDS)
				{
					var b = Board as FDS;
					ControllerDefinition.BoolButtons.Add("FDS Eject");
					for (int i = 0; i < b.NumSides; i++)
						ControllerDefinition.BoolButtons.Add("FDS Insert " + i);
				}

				if (_isVS)
				{
					ControllerDefinition.BoolButtons.Add("Insert Coin P1");
					ControllerDefinition.BoolButtons.Add("Insert Coin P2");
					ControllerDefinition.BoolButtons.Add("Service Switch");
				}
			}

			// don't replace the magicSoundProvider on reset, as it's not needed
			// if (magicSoundProvider != null) magicSoundProvider.Dispose();

			// set up region
			switch (_display_type)
			{
				case Common.DisplayType.PAL:
					apu = new APU(this, apu, true);
					ppu.region = PPU.Region.PAL;
					CoreComm.VsyncNum = 50;
					CoreComm.VsyncDen = 1;
					cpuclockrate = 1662607;
					cpu_sequence = cpu_sequence_PAL;
					_display_type = DisplayType.PAL;
					break;
				case Common.DisplayType.NTSC:
					apu = new APU(this, apu, false);
					ppu.region = PPU.Region.NTSC;
					CoreComm.VsyncNum = 39375000;
					CoreComm.VsyncDen = 655171;
					cpuclockrate = 1789773;
					cpu_sequence = cpu_sequence_NTSC;
					break;
				// this is in bootgod, but not used at all
				case Common.DisplayType.DENDY:
					apu = new APU(this, apu, false);
					ppu.region = PPU.Region.Dendy;
					CoreComm.VsyncNum = 50;
					CoreComm.VsyncDen = 1;
					cpuclockrate = 1773448;
					cpu_sequence = cpu_sequence_NTSC;
					_display_type = DisplayType.DENDY;
					break;
				default:
					throw new Exception("Unknown displaytype!");
			}
			if (magicSoundProvider == null)
				magicSoundProvider = new MagicSoundProvider(this, (uint)cpuclockrate);

			BoardSystemHardReset();

			// apu has some specific power up bahaviour that we will emulate here
			apu.NESHardReset();


			if (SyncSettings.InitialWRamStatePattern != null && SyncSettings.InitialWRamStatePattern.Any())
			{
				for (int i = 0; i < 0x800; i++)
				{
					ram[i] = SyncSettings.InitialWRamStatePattern[i % SyncSettings.InitialWRamStatePattern.Count];
				}
			}
			else
			{
				// check fceux's PowerNES and FCEU_MemoryRand function for more information:
				// relevant games: Cybernoid; Minna no Taabou no Nakayoshi Daisakusen; Huang Di; and maybe mechanized attack
				for (int i = 0; i < 0x800; i++)
				{
					if ((i & 4) != 0)
					{
						ram[i] = 0xFF;
					}
					else
					{
						ram[i] = 0x00;
					}
				}
			}

			SetupMemoryDomains();

			//in this emulator, reset takes place instantaneously
			cpu.PC = (ushort)(ReadMemory(0xFFFC) | (ReadMemory(0xFFFD) << 8));
			cpu.P = 0x34;
			cpu.S = 0xFD;

			// some boards cannot have specific values in RAM upon initialization
			// Let's hard code those cases here
			// these will be defined through the gameDB exclusively for now.

			if (cart.DB_GameInfo!=null)
			{
				
				if (cart.DB_GameInfo.Hash == "60FC5FA5B5ACCAF3AEFEBA73FC8BFFD3C4DAE558" // Camerica Golden 5
					|| cart.DB_GameInfo.Hash == "BAD382331C30B22A908DA4BFF2759C25113CC26A" // Camerica Golden 5
					|| cart.DB_GameInfo.Hash == "40409FEC8249EFDB772E6FFB2DCD41860C6CCA23" // Camerica Pegasus 4-in-1
					)
				{
					ram[0x701] = 0xFF;
				}
			}

		}

		bool resetSignal;
		bool hardResetSignal;
		public void FrameAdvance(bool render, bool rendersound)
		{
			if (Tracer.Enabled)
				cpu.TraceCallback = (s) => Tracer.Put(s);
			else
				cpu.TraceCallback = null;

			lagged = true;
			if (resetSignal)
			{
				Board.NESSoftReset();
				cpu.NESSoftReset();
				apu.NESSoftReset();
				ppu.NESSoftReset();
			}
			else if (hardResetSignal)
			{
				HardReset();
			}

			Frame++;

			//if (resetSignal)
			//Controller.UnpressButton("Reset");   TODO fix this
			resetSignal = Controller["Reset"];
			hardResetSignal = Controller["Power"];

			if (Board is FDS)
			{
				var b = Board as FDS;
				if (Controller["FDS Eject"])
					b.Eject();
				for (int i = 0; i < b.NumSides; i++)
					if (Controller["FDS Insert " + i])
						b.InsertSide(i);
			}

			if (_isVS)
			{
				if (controller["Service Switch"])
					VS_service = 1;
				else
					VS_service = 0;

				if (controller["Insert Coin P1"])
					VS_coin_inserted |= 1;
				else
					VS_coin_inserted &= 2;

				if (controller["Insert Coin P2"])
					VS_coin_inserted |= 2;
				else
					VS_coin_inserted &= 1;
			}

			ppu.FrameAdvance();
			if (lagged)
			{
				_lagcount++;
				islag = true;
			}
			else
				islag = false;

			videoProvider.FillFrameBuffer();
		}

		//PAL:
		//0 15 30 45 60 -> 12 27 42 57 -> 9 24 39 54 -> 6 21 36 51 -> 3 18 33 48 -> 0
		//sequence of ppu clocks per cpu clock: 3,3,3,3,4
		//at least it should be, but something is off with that (start up time?) so it is 3,3,3,4,3 for now
		//NTSC:
		//sequence of ppu clocks per cpu clock: 3
		ByteBuffer cpu_sequence;
		static ByteBuffer cpu_sequence_NTSC = new ByteBuffer(new byte[] { 3, 3, 3, 3, 3 });
		static ByteBuffer cpu_sequence_PAL = new ByteBuffer(new byte[] { 3, 3, 3, 4, 3 });
		public int cpu_step, cpu_stepcounter, cpu_deadcounter;

		public int oam_dma_index;
		public bool oam_dma_exec = false;
		public ushort oam_dma_addr;
		public byte oam_dma_byte;
		public bool dmc_dma_exec = false;
		public bool dmc_realign;
		public bool IRQ_delay;
		public bool special_case_delay; // very ugly but the only option
		public bool do_the_reread;

#if VS2012
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
		internal void RunCpuOne()
		{
			cpu_stepcounter++;
			if (cpu_stepcounter == cpu_sequence[cpu_step])
			{
				cpu_step++;
				if (cpu_step == 5) cpu_step = 0;
				cpu_stepcounter = 0;



				///////////////////////////
				// OAM DMA start
				///////////////////////////

				if (sprdma_countdown > 0)
				{
					sprdma_countdown--;
					if (sprdma_countdown == 0)
					{
						if (cpu.TotalExecutedCycles % 2 == 0)
						{
							cpu_deadcounter = 2;
						}
						else
						{
							cpu_deadcounter = 1;
						}
						oam_dma_exec = true;
						cpu.RDY = false;
						oam_dma_index = 0;
						special_case_delay = true;
					}
				}

				if (oam_dma_exec && apu.dmc_dma_countdown != 1 && !dmc_realign)
				{
					if (cpu_deadcounter == 0)
					{

						if (oam_dma_index % 2 == 0)
						{
							oam_dma_byte = ReadMemory(oam_dma_addr);
							oam_dma_addr++;
						}
						else
						{
							WriteMemory(0x2004, oam_dma_byte);
						}
						oam_dma_index++;
						if (oam_dma_index == 512) oam_dma_exec = false;

					}
					else
					{
						cpu_deadcounter--;
					}
				}
				else if (apu.dmc_dma_countdown == 1)
				{
					dmc_realign = true;
				}
				else if (dmc_realign)
				{
					dmc_realign = false;
				}
				/////////////////////////////
				// OAM DMA end
				/////////////////////////////


				/////////////////////////////
				// dmc dma start
				/////////////////////////////

				if (apu.dmc_dma_countdown > 0)
				{
					cpu.RDY = false;
					dmc_dma_exec = true;
					apu.dmc_dma_countdown--;
					if (apu.dmc_dma_countdown == 0)
					{
						apu.RunDMCFetch();
						dmc_dma_exec = false;
						apu.dmc_dma_countdown = -1;
						do_the_reread = true;
					}
				}

				/////////////////////////////
				// dmc dma end
				/////////////////////////////
				apu.RunOne(true);

				if (cpu.RDY && !IRQ_delay)
				{
					cpu.IRQ = _irq_apu || Board.IRQSignal;
				}
				else if (special_case_delay || apu.dmc_dma_countdown == 3)
				{
					cpu.IRQ = _irq_apu || Board.IRQSignal;
					special_case_delay = false;
				}


				cpu.ExecuteOne();
				apu.RunOne(false);

				if (ppu.double_2007_read > 0)
					ppu.double_2007_read--;

				if (do_the_reread && cpu.RDY)
					do_the_reread = false;

				if (IRQ_delay)
					IRQ_delay = false;

				if (!dmc_dma_exec && !oam_dma_exec && !cpu.RDY)
				{
					cpu.RDY = true;
					IRQ_delay = true;
				}



				ppu.ppu_open_bus_decay(0);

				Board.ClockCPU();
				ppu.PostCpuInstructionOne();
			}
		}

#if VS2012
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
		public byte ReadReg(int addr)
		{
			byte ret_spec;
			switch (addr)
			{
				case 0x4000:
				case 0x4001:
				case 0x4002:
				case 0x4003:
				case 0x4004:
				case 0x4005:
				case 0x4006:
				case 0x4007:
				case 0x4008:
				case 0x4009:
				case 0x400A:
				case 0x400B:
				case 0x400C:
				case 0x400D:
				case 0x400E:
				case 0x400F:
				case 0x4010:
				case 0x4011:
				case 0x4012:
				case 0x4013:
					return DB;
				//return apu.ReadReg(addr);
				case 0x4014: /*OAM DMA*/ break;
				case 0x4015: return (byte)((byte)(apu.ReadReg(addr) & 0xDF) + (byte)(DB & 0x20));
				case 0x4016:
					if (_isVS)
					{
						byte ret = 0;
						// for whatever reason, in VS left and right controller have swapped regs
						ret = read_joyport(0x4017);
						ret &= 1;
						ret = (byte)(ret | (VS_service << 2) | (VS_dips[0] << 3) | (VS_dips[1] << 4) | (VS_coin_inserted << 5) | (VS_ROM_control<<7));

						return ret;
					}
					else
					{
						// special hardware glitch case
						ret_spec = read_joyport(addr);
						if (do_the_reread)
						{
							ret_spec = read_joyport(addr);
							do_the_reread = false;
						}
						return ret_spec;

					}
				case 0x4017:
					{
						if (_iskeyboard)
						{
							// eventually this will be the keyboard function, but for now it is a place holder (no keys pressed)
							return 0x1E;
						}
						else if (_isVS)
						{
							byte ret = 0;
							// for whatever reason, in VS left and right controller have swapped regs
							ret = read_joyport(0x4016);
							ret &= 1;

							ret = (byte)(ret | (VS_dips[2] << 2) | (VS_dips[3] << 3) | (VS_dips[4] << 4) | (VS_dips[5] << 5) | (VS_dips[6] << 6) | (VS_dips[7] << 7));

							return ret;

						}
						else
						{
							return read_joyport(addr);
						}
					}
				default:
					//Console.WriteLine("read register: {0:x4}", addr);
					break;

			}
			return DB;
		}

		public byte PeekReg(int addr)
		{
			switch (addr)
			{
				case 0x4000:
				case 0x4001:
				case 0x4002:
				case 0x4003:
				case 0x4004:
				case 0x4005:
				case 0x4006:
				case 0x4007:
				case 0x4008:
				case 0x4009:
				case 0x400A:
				case 0x400B:
				case 0x400C:
				case 0x400D:
				case 0x400E:
				case 0x400F:
				case 0x4010:
				case 0x4011:
				case 0x4012:
				case 0x4013:
					return apu.PeekReg(addr);
				case 0x4014: /*OAM DMA*/ break;
				case 0x4015: return apu.PeekReg(addr);
				case 0x4016:
				case 0x4017:
					return peek_joyport(addr);
				default:
					//Console.WriteLine("read register: {0:x4}", addr);
					break;

			}
			return 0xFF;
		}

		void WriteReg(int addr, byte val)
		{
			switch (addr)
			{
				case 0x4000:
				case 0x4001:
				case 0x4002:
				case 0x4003:
				case 0x4004:
				case 0x4005:
				case 0x4006:
				case 0x4007:
				case 0x4008:
				case 0x4009:
				case 0x400A:
				case 0x400B:
				case 0x400C:
				case 0x400D:
				case 0x400E:
				case 0x400F:
				case 0x4010:
				case 0x4011:
				case 0x4012:
				case 0x4013:
					apu.WriteReg(addr, val);
					break;
				case 0x4014: Exec_OAMDma(val); break;
				case 0x4015: apu.WriteReg(addr, val); break;
				case 0x4016:
					if (_iskeyboard)
					{
						// eventually keyboard emulation will go here
					}
					else if (_isVS)
					{
						write_joyport(val);
						VS_chr_reg = (byte)((val & 0x4)>>2);

						//TODO: does other stuff for dual system

						//this is actually different then assignment
						VS_prg_reg = (byte)((val & 0x4)>>2);

					}
					else
					{
						write_joyport(val);
					}
					break;
				case 0x4017: apu.WriteReg(addr, val); break;
				default:
					//Console.WriteLine("wrote register: {0:x4} = {1:x2}", addr, val);
					break;
			}
		}

		void write_joyport(byte value)
		{
			var si = new StrobeInfo(latched4016, value);
			ControllerDeck.Strobe(si, Controller);
			latched4016 = value;
		}

		byte read_joyport(int addr)
		{
			InputCallbacks.Call();
			lagged = false;
			byte ret = addr == 0x4016 ? ControllerDeck.ReadA(Controller) : ControllerDeck.ReadB(Controller);
			ret &= 0x1f;
			ret |= (byte)(0xe0 & DB);
			return ret;
		}

		byte peek_joyport(int addr)
		{
			// at the moment, the new system doesn't support peeks
			return 0;
		}

		void Exec_OAMDma(byte val)
		{
			//schedule a sprite dma event for beginning 1 cycle in the future.
			//this receives 2 because thats just the way it works out.
			oam_dma_addr = (ushort)(val << 8);

			sprdma_countdown = 1;
		}

		/// <summary>
		/// Sets the provided palette as current.
		/// Applies the current deemph settings if needed to expand a 64-entry palette to 512
		/// </summary>
		public void SetPalette(byte[,] pal)
		{
			int nColors = pal.GetLength(0);
			int nElems = pal.GetLength(1);

			if (nColors == 512)
			{
				//just copy the palette directly
				for (int c = 0; c < 64 * 8; c++)
				{
					int r = pal[c, 0];
					int g = pal[c, 1];
					int b = pal[c, 2];
					palette_compiled[c] = (int)unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
				}
			}
			else
			{
				//expand using deemph
				for (int i = 0; i < 64 * 8; i++)
				{
					int d = i >> 6;
					int c = i & 63;
					int r = pal[c, 0];
					int g = pal[c, 1];
					int b = pal[c, 2];
					Palettes.ApplyDeemphasis(ref r, ref g, ref b, d);
					palette_compiled[i] = (int)unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
				}
			}
		}

		/// <summary>
		/// looks up an internal NES pixel value to an rgb int (applying the core's current palette and assuming no deemph)
		/// </summary>
		public int LookupColor(int pixel)
		{
			return palette_compiled[pixel];
		}

		public byte DummyReadMemory(ushort addr) { return 0; }

		private void ApplySystemBusPoke(int addr, byte value)
		{
			if (addr < 0x2000)
			{
				ram[(addr & 0x7FF)] = value;
			}
			else if (addr < 0x4000)
			{
				ppu.WriteReg((addr & 0x07), value);
			}
			else if (addr < 0x4020)
			{
				WriteReg(addr, value);
			}
			else
			{
				ApplyGameGenie(addr, value, null); //Apply a cheat to the remaining regions since they have no direct access, this may not be the best way to handle this situation
			}
		}

		public byte PeekMemory(ushort addr)
		{
			byte ret;

			if (addr >= 0x4020)
			{
				ret = Board.PeekCart(addr); //easy optimization, since rom reads are so common, move this up (reordering the rest of these elseifs is not easy)
			}
			else if (addr < 0x0800)
			{
				ret = ram[addr];
			}
			else if (addr < 0x2000)
			{
				ret = ram[addr & 0x7FF];
			}
			else if (addr < 0x4000)
			{
				ret = Board.PeekReg2xxx(addr);
			}
			else if (addr < 0x4020)
			{
				ret = PeekReg(addr); //we're not rebasing the register just to keep register names canonical
			}
			else
			{
				throw new Exception("Woopsie-doodle!");
				ret = 0xFF;
			}

			return ret;
		}

		//old data bus values from previous reads
		public byte DB;

		public void ExecFetch(ushort addr)
		{
			MemoryCallbacks.CallExecutes(addr);
		}

		public byte ReadMemory(ushort addr)
		{
			byte ret;

			if (addr >= 0x8000)
			{
				ret = Board.ReadPRG(addr - 0x8000); //easy optimization, since rom reads are so common, move this up (reordering the rest of these elseifs is not easy)
			}
			else if (addr < 0x0800)
			{
				ret = ram[addr];
			}
			else if (addr < 0x2000)
			{
				ret = ram[addr & 0x7FF];
			}
			else if (addr < 0x4000)
			{
				ret = Board.ReadReg2xxx(addr);
			}
			else if (addr < 0x4020)
			{
				ret = ReadReg(addr); //we're not rebasing the register just to keep register names canonical			
			}
			else if (addr < 0x6000)
			{
				ret = Board.ReadEXP(addr - 0x4000);
			}
			else
			{
				ret = Board.ReadWRAM(addr - 0x6000);
			}

			//handle breakpoints and stuff.
			//the idea is that each core can implement its own watch class on an address which will track all the different kinds of monitors and breakpoints and etc.
			//but since freeze is a common case, it was implemented through its own mechanisms
			if (sysbus_watch[addr] != null)
			{
				sysbus_watch[addr].Sync();
				ret = sysbus_watch[addr].ApplyGameGenie(ret);
			}

			MemoryCallbacks.CallReads(addr);

			DB = ret;
			return ret;
		}

		public void ApplyGameGenie(int addr, byte value, byte? compare)
		{
			if (addr < sysbus_watch.Length)
			{
				GetWatch(NESWatch.EDomain.Sysbus, addr).SetGameGenie(compare, value);
			}
		}

		public void RemoveGameGenie(int addr)
		{
			if (addr < sysbus_watch.Length)
			{
				GetWatch(NESWatch.EDomain.Sysbus, addr).RemoveGameGenie();
			}
		}

		public void WriteMemory(ushort addr, byte value)
		{
			if (addr < 0x0800)
			{
				ram[addr] = value;
			}
			else if (addr < 0x2000)
			{
				ram[addr & 0x7FF] = value;
			}
			else if (addr < 0x4000)
			{
				Board.WriteReg2xxx(addr, value);
			}
			else if (addr < 0x4020)
			{
				WriteReg(addr, value);  //we're not rebasing the register just to keep register names canonical
			}
			else if (addr < 0x6000)
			{
				Board.WriteEXP(addr - 0x4000, value);
			}
			else if (addr < 0x8000)
			{
				Board.WriteWRAM(addr - 0x6000, value);
			}
			else
			{
				Board.WritePRG(addr - 0x8000, value);
			}

			MemoryCallbacks.CallWrites(addr);
		}

		//the palette for each VS game needs to be chosen explicitly since there are 6 different ones.
		//let's do it here where it's easy to keep track of
		public void VS_pick_palette()
		{
			if (cart.DB_GameInfo.Hash == "C145803B5FEE71172A890606A44C6D5DF6D2FA8F" // VS Star Luster
				|| cart.DB_GameInfo.Hash == "D232F7BE509E3B745D9E9803DA945C3FABA37A70" // VS Ninja Jajamurru Kun
				|| cart.DB_GameInfo.Hash == "CAE9CB4C0452C56BED58AEACCEACE8A3107F843A" // mighty bomb jack
				|| cart.DB_GameInfo.Hash == "F08357458FF1DBFEBE152CAE100ACEF62F84774B" // VS Stroke and Match [!] (J)
				|| cart.DB_GameInfo.Hash == "21674A6571F0D4C812B9C30092C0C5ABED0C92E1" // VS gumshoe

			)
				SetPalette(Palettes.palette_2c03_2c05);

			if (cart.DB_GameInfo.Hash == "1A4EC64E576BAD64DAF320AEED0BE1B8B50D21DF" // VS Pinball
				|| cart.DB_GameInfo.Hash == "E0572DA111D05BF622EC137DF8A658F7B0687DDF" // VS Battle City
				|| cart.DB_GameInfo.Hash == "B7FD645A523E57864024369BA7201D851842CC5A" // VS Battle City [p2]
				|| cart.DB_GameInfo.Hash == "B0D1852782B4E9A9CCC2BA24CD40B170C38B940F" // VS Gradius
				|| cart.DB_GameInfo.Hash == "0C0E33BE229E36229EFA12912285A3ED2858D9F9" // VS Gradius [b1]

			)
				SetPalette(Palettes.palette_2c04_001);

			if (cart.DB_GameInfo.Hash == "4E38B4C231C44BB1408AC6C6F941A136DD33D0EB" // VS Platoon
				|| cart.DB_GameInfo.Hash == "F8A0F2C5A4B7212CB35F53EA7193B3DD85D6E1CD" // VS Mach Rider
				|| cart.DB_GameInfo.Hash == "F8ED6FAFA057DBEEB0398EECCC9DE91747D479AD" // VS Mach Rider [b1]
				|| cart.DB_GameInfo.Hash == "CDE1ECAF212A9F5A5A49F904F87951EDA15D54DD" // VS Stroke and Match Golf (Ladies)
				|| cart.DB_GameInfo.Hash == "E0F7BDBD2C96B14D4B8D2146A900AAAD17F9E3B1" // VS Stroke and Match Golf (Mens)
				|| cart.DB_GameInfo.Hash == "136DB00766AF39AF9E4FAB7306A3346C2F062446" // VS Stroke and Match Golf [a1][b1]
				|| cart.DB_GameInfo.Hash == "FBA72452977AE8602B628B39AC4A5A7A6FD0F92F" // VS Stroke and Match Golf [a1][b2]
				

			)
				SetPalette(Palettes.palette_2c04_002);

			if (cart.DB_GameInfo.Hash == "1CF6AA8625AD1558E1F1AAF2E1710D5A09A2CED0" // VS Excite Bike (US)
				|| cart.DB_GameInfo.Hash == "BBB0AF27B313D7C838A38FB772A6FE8AFBAFBB95" // VS Soccer (US)
				|| cart.DB_GameInfo.Hash == "730AFCD33209469D4F2B2B0ABBF86A22AA052609" // VS Goonies
				|| cart.DB_GameInfo.Hash == "B73D62711B55F5B1065AC6F352A4F6AECD91A731" // VS Goonies [b1]
				|| cart.DB_GameInfo.Hash == "C5534A442BE65436B3FCB8A2ED0129354ED42DF1" // VS Goonies [b2]
				|| cart.DB_GameInfo.Hash == "726E10E484FCEFA32EDD531AFE4EEBB9F9F8C536" // VS Goonies [o1]


			)
				SetPalette(Palettes.palette_2c04_003);

			if (cart.DB_GameInfo.Hash == "B21AA940728ED80C72EE23C251C96E42CC84B2D6" // VS Super Mario Bros
				|| cart.DB_GameInfo.Hash == "9F1943AADE4233285589CEA5BDC96B5380D49337" // VS Ice Climber (USA)
				|| cart.DB_GameInfo.Hash == "15BECCD6CA1D165B23531CF5CFAC35C327328335" // VS Ice Climber (USA) [o1]

				|| cart.DB_GameInfo.Hash == "8885F4F00C0B73C156179BCEABA5381487DBEAAD" // VS Spy Vs. Spy (J)[!]
				|| cart.DB_GameInfo.Hash == "56FB1AAABA3B8C05452B2D5B8F232FAFB64AC70D" // VS Excite Bike (US) [a1]
				|| cart.DB_GameInfo.Hash == "7FD66E0A4CC0E404F404D8164FA221EE2ACB7A38" // VS Clu Clu Land
				


			)
				SetPalette(Palettes.palette_2c04_004);

		}

	}
}
