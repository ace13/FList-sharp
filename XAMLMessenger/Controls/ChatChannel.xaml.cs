﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using libflist.FChat;
using libflist.Info;
using System.Globalization;
using XAMLMessenger.Message;
using libflist.Message;
using libflist.Message.Nodes;

namespace XAMLMessenger.Controls
{
    /// <summary>
    /// Interaction logic for ChatChannel.xaml
    /// </summary>
    public partial class ChatChannel : UserControl
    {
        Channel _channel;
        public IReadOnlyCollection<libflist.Character> Users => _channel.Characters;
        public Channel Channel => _channel;

        public ChatChannel() : this(null)
        {
        }

        public ChatChannel(Channel chan)
        {
            _channel = chan;
            if (_channel != null)
            {
                _channel.Connection.OnChannelUserJoin += (s, e) =>
                {
                    if (e.Channel == _channel)
                    {
                        _userList.Document.Blocks.Add(new Paragraph(new UserNode() { Text = e.Character.Name }.ToInline(_channel))
                        {
                            Tag = e.Character
                        });
                    }
                };
                _channel.Connection.OnChannelUserLeave += (s, e) =>
                {
                    if (e.Channel == _channel)
                        _userList.Document.Blocks.Remove(_userList.Document.Blocks.First(b => b.Tag == e.Character));
                };
                _channel.Connection.OnChannelChatMessage += (s, e) =>
                {
                    if (e.Channel == _channel)
                        AddMessage(e.Character, e.Message);
                };
            }

            InitializeComponent();
        }

        void baseMessage(Paragraph par, Character sender, string message)
        {
            // TODO: Hilighting
            if (sender == App.Current.FChatClient.LocalCharacter)
                par.Background = new SolidColorBrush
                {
                    Color = Colors.Black,
                    Opacity = 0.1
                };

            par.Inlines.AddRange(new Inline[]{
                new DateNode().ToInline(_channel),
                new UserNode { Text = sender.Name }.ToInline(_channel)
            });

            par.Inlines.AddRange(new Parser { Validity = NodeValidity.FChat }.ParseMessage(message).Select(n => n.ToInline(_channel)));
        }

        public void AddAction(Character sender, string action)
        {
            var par = new Paragraph();
            par.FontStyle = FontStyles.Italic;
            par.Foreground = Brushes.White;

            baseMessage(par, sender, action);

			_messageList.AddMessageParagraph(par);
        }
        public void AddMessage(Character sender, string message)
        {
            var par = new Paragraph();
            par.Foreground = Brushes.White;

            baseMessage(par, sender, $": {message}");

			_messageList.AddMessageParagraph(par);
        }
        public void AddSYSMessage(string message)
        {
            _messageList.AddMessage($"System: {message}");
        }
        public void AddLFRPMessage(Character sender, string message)
        {
            var par = new Paragraph();
            par.Foreground = Brushes.White;
            par.Background = new SolidColorBrush
            {
                Color = Colors.DarkGreen,
                Opacity = 0.1
            };

            baseMessage(par, sender, $": {message}");

			_messageList.AddMessageParagraph(par);
        }
    }

    public class HalfConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
              object parameter, CultureInfo culture)
        {
            return (double)value / 2;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            return (double)value * 2;
        }
    }
}
