﻿using System;
using System.ComponentModel;
using System.Globalization;

using BizHawk.Common;
using BizHawk.Emulation.Common;
using Newtonsoft.Json;


namespace BizHawk.Emulation.Cores.Consoles.Sega.gpgx
{
	public partial class GPGX : ISettable<GPGX.GPGXSettings, GPGX.GPGXSyncSettings>
	{
		public GPGXSettings GetSettings()
		{
			return _settings.Clone();
		}

		public GPGXSyncSettings GetSyncSettings()
		{
			return _syncSettings.Clone();
		}

		public PutSettingsDirtyBits PutSettings(GPGXSettings o)
		{
			bool ret = GPGXSettings.NeedsReboot(_settings, o);
			_settings = o;
			Core.gpgx_set_draw_mask(_settings.GetDrawMask());
			return ret ? PutSettingsDirtyBits.RebootCore : PutSettingsDirtyBits.None;
		}

		public PutSettingsDirtyBits PutSyncSettings(GPGXSyncSettings o)
		{
			bool ret = GPGXSyncSettings.NeedsReboot(_syncSettings, o);
			_syncSettings = o;
			return ret ? PutSettingsDirtyBits.RebootCore : PutSettingsDirtyBits.None;
		}

		private class UintToHexConverter : TypeConverter
		{
			public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
			{
				return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
			}

			public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
			{
				return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
			}

			public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
			{
				if (destinationType == typeof(string) && value.GetType() == typeof(uint))
				{
					return $"0x{value:x8}";
				}

				return base.ConvertTo(context, culture, value, destinationType);
			}

			public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
			{
				if (value.GetType() == typeof(string))
				{
					string input = (string)value;
					if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
					{
						input = input.Substring(2);
					}
					return uint.Parse(input, NumberStyles.HexNumber, culture);
				}

				return base.ConvertFrom(context, culture, value);
			}
		}

		private class UshortToHexConverter : TypeConverter
		{
			public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
			{
				return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
			}

			public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
			{
				return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
			}

			public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
			{
				if (destinationType == typeof(string) && value.GetType() == typeof(ushort))
				{
					return $"0x{value:x4}";
				}

				return base.ConvertTo(context, culture, value, destinationType);
			}

			public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
			{
				if (value.GetType() == typeof(string))
				{
					string input = (string)value;
					if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
					{
						input = input.Substring(2);
					}
					return ushort.Parse(input, NumberStyles.HexNumber, culture);
				}

				return base.ConvertFrom(context, culture, value);
			}
		}

		private GPGXSyncSettings _syncSettings;
		private GPGXSettings _settings;

		public class GPGXSettings
		{
			[DeepEqualsIgnore]
			[JsonIgnore]
			private bool _DrawBGA;

			[DisplayName("Background Layer A")]
			[Description("True to draw BG layer A")]
			[DefaultValue(true)]
			public bool DrawBGA
			{
				get => _DrawBGA;
				set => _DrawBGA = value;
			}

			[DeepEqualsIgnore]
			[JsonIgnore]
			private bool _DrawBGB;

			[DisplayName("Background Layer B")]
			[Description("True to draw BG layer B")]
			[DefaultValue(true)]
			public bool DrawBGB
			{
				get => _DrawBGB;
				set => _DrawBGB = value;
			}

			[DeepEqualsIgnore]
			[JsonIgnore]
			private bool _DrawBGW;

			[DisplayName("Background Layer W")]
			[Description("True to draw BG layer W")]
			[DefaultValue(true)]
			public bool DrawBGW
			{
				get => _DrawBGW;
				set => _DrawBGW = value;
			}

			[DeepEqualsIgnore]
			[JsonIgnore]
			private bool _DrawObj;

			[DisplayName("Sprite Layer")]
			[Description("True to draw sprite layer")]
			[DefaultValue(true)]
			public bool DrawObj
			{
				get => _DrawObj;
				set => _DrawObj = value;
			}

			[DeepEqualsIgnore]
			[JsonIgnore]
			private bool _PadScreen320;

			[DisplayName("Pad screen to 320")]
			[Description("When using 1:1 aspect ratio, enable to make screen width constant (320) between game modes")]
			[DefaultValue(false)]
			public bool PadScreen320
			{
				get => _PadScreen320;
				set => _PadScreen320 = value;
			}


			[DeepEqualsIgnore]
			[JsonIgnore]
			private bool _Backdrop;

			[DisplayName("Use custom backdrop color")]
			[Description("Filler when layers are off")]
			[DefaultValue((bool)false)]
			public bool Backdrop
			{
				get => _Backdrop;
				set => _Backdrop = value;
			}


			public GPGXSettings()
			{
				SettingsUtil.SetDefaultValues(this);
			}

			public GPGXSettings Clone()
			{
				return (GPGXSettings)MemberwiseClone();
			}

			public LibGPGX.DrawMask GetDrawMask()
			{
				LibGPGX.DrawMask ret = 0;
				if (DrawBGA) ret |= LibGPGX.DrawMask.BGA;
				if (DrawBGB) ret |= LibGPGX.DrawMask.BGB;
				if (DrawBGW) ret |= LibGPGX.DrawMask.BGW;
				if (DrawObj) ret |= LibGPGX.DrawMask.Obj;
				if (Backdrop) ret |= LibGPGX.DrawMask.Backdrop;
				return ret;
			}

			public static bool NeedsReboot(GPGXSettings x, GPGXSettings y)
			{
				return !DeepEquality.DeepEquals(x, y);
			}

			
		}

		public class GPGXSyncSettings
		{
			[DisplayName("Use Six Button Controllers")]
			[Description("Controls the type of any attached normal controllers; six button controllers are used if true, otherwise three button controllers.  Some games don't work correctly with six button controllers.  Not relevant if other controller types are connected.")]
			[DefaultValue(false)]
			public bool UseSixButton { get; set; }

			[DisplayName("Control Type - Left Port")]
			[Description("Sets the type of controls that are plugged into the console.  Some games will automatically load with a different control type.")]
			[DefaultValue(ControlType.Normal)]
			public ControlType ControlTypeLeft { get; set; }

			[DisplayName("Control Type - Right Port")]
			[Description("Sets the type of controls that are plugged into the console.  Some games will automatically load with a different control type.")]
			[DefaultValue(ControlType.Normal)]
			public ControlType ControlTypeRight { get; set; }

			[DisplayName("Autodetect Region")]
			[Description("Sets the region of the emulated console.  Many games can run on multiple regions and will behave differently on different ones.  Some games may require a particular region.")]
			[DefaultValue(LibGPGX.Region.Autodetect)]
			public LibGPGX.Region Region { get; set; }

			[DisplayName("Audio Filter")]
			[DefaultValue(LibGPGX.InitSettings.FilterType.LowPass)]
			public LibGPGX.InitSettings.FilterType Filter { get; set; }

			[DisplayName("Low Pass Range")]
			[Description("Only active when filter type is lowpass. Range is 0 - 0xffff. Default value is 40%")]
			[TypeConverter(typeof(UshortToHexConverter))]
			[DefaultValue((ushort)0x6666)]
			public ushort LowPassRange { get; set; }

			[DisplayName("Three band low cutoff")]
			[Description("Only active when filter type is three band")]
			[DefaultValue((short)880)]
			public short LowFreq { get; set; }

			[DisplayName("Three band high cutoff")]
			[Description("Only active when filter type is three band")]
			[DefaultValue((short)5000)]
			public short HighFreq { get; set; }

			[DisplayName("Three band low gain")]
			[Description("Only active when filter type is three band")]
			[DefaultValue(1f)]
			public float LowGain { get; set; }

			[DisplayName("Three band mid gain")]
			[Description("Only active when filter type is three band")]
			[DefaultValue(1f)]
			public float MidGain { get; set; }

			[DisplayName("Three band high gain")]
			[Description("Only active when filter type is three band")]
			[DefaultValue(1f)]
			public float HighGain { get; set; }

			[Description("Magic pink by default. Requires core reboot")]
			[TypeConverter(typeof(UintToHexConverter))]
			[DefaultValue((uint)0xffff00ff)]
			public uint BackdropColor { get; set; }

			public LibGPGX.InitSettings GetNativeSettings(GameInfo game)
			{
				return new LibGPGX.InitSettings
				{
					Filter = Filter,
					LowPassRange = LowPassRange,
					LowFreq = LowFreq,
					HighFreq = HighFreq,
					LowGain = (short)(LowGain * 100),
					MidGain = (short)(MidGain * 100),
					HighGain = (short)(HighGain * 100),
					BackdropColor = BackdropColor,
					SixButton = UseSixButton,
					InputSystemA = SystemForSystem(ControlTypeLeft),
					InputSystemB = SystemForSystem(ControlTypeRight),
					Region = Region,
					ForceSram = game["sram"],
				};
			}

			public GPGXSyncSettings()
			{
				SettingsUtil.SetDefaultValues(this);
			}

			public GPGXSyncSettings Clone()
			{
				return (GPGXSyncSettings)MemberwiseClone();
			}

			public static bool NeedsReboot(GPGXSyncSettings x, GPGXSyncSettings y)
			{
				return !DeepEquality.DeepEquals(x, y);
			}
		}
	}
}
