﻿using ConsoleMessenger.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;

namespace ConsoleMessenger.Commands
{
	[Command("set", Description = "Reads or sets the value of a configurable setting.")]
	class Set : Command
	{
		class PropertyKV
		{
			public object Class;
			public PropertyInfo Property;
		}

        IEnumerable<PropertyKV> _AvailableSettings
        {
            get
            {
                var settings = typeof(Application).GetProperties(BindingFlags.Static | BindingFlags.Public).Where(p => p.GetCustomAttribute<SettingAttribute>() != null).Select(p => new PropertyKV { Property = p, Class = null });

                if (Application.CurrentChannelBuffer != null)
                    settings = settings.Concat(Application.CurrentChannelBuffer.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetCustomAttribute<SettingAttribute>() != null).Select(p => new PropertyKV { Class = Application.CurrentChannelBuffer, Property = p }));

                return settings;
            }
        }

		public void Call()
		{
			Debug.WriteLine("Available settings:");
			foreach (var prop in _AvailableSettings)
			{
				var set = prop.Property.GetCustomAttribute<SettingAttribute>();
				Debug.WriteLine($"- {set.Name} = {prop.Property.GetMethod.Invoke(prop.Class, null)} - {set.Description}");
			}	
		}

		public void Call(string name)
		{
            var setting = _AvailableSettings.FirstOrDefault(s => s.Property.GetCustomAttribute<SettingAttribute>().Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

            if (setting == null)
                Debug.WriteLine("No such setting {0}", name);
            else
            {
                var att = setting.Property.GetCustomAttribute<SettingAttribute>();
                Debug.WriteLine($"{att.Name} = {setting.Property.GetValue(setting.Class)} - {att.Description}");
            }
		}

		public void Call(string name, params string[] values)
		{
            var setting = _AvailableSettings.FirstOrDefault(s => s.Property.GetCustomAttribute<SettingAttribute>().Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

            if (setting == null)
                Debug.WriteLine("No such setting {0}", name);
            else
            {
                var att = setting.Property.GetCustomAttribute<SettingAttribute>();
                var conv = TypeDescriptor.GetConverter(setting.Property.PropertyType);

                setting.Property.SetValue(setting.Class, conv.ConvertFromString(values.ToString(" ")));
                Debug.WriteLine($"{att.Name} = {setting.Property.GetValue(setting.Class)} - {att.Description}");
            }
		}
	}
}
