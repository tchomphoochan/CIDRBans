using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;

using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace CIDRBans
{
    [ApiVersion(2, 0)]
    public class CIDRBansPlugin : TerrariaPlugin
    {
        public override string Name { get { return "CIDR Bans"; } }
        public override string Description { get { return "Allows banning CIDR ranges"; } }
        public override string Author { get { return "AquaBlitz11"; } }
        public override Version Version { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public CIDRBansPlugin(Main game) : base(game) { }
        
        private CIDRBanManager cidrbans;
        private const string rangeregex = @"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\/(3[0-2]|[1-2]?[0-9])$";
        private const string ipregex = @"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}$";

        public override void Initialize()
        {
            ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);
            ServerApi.Hooks.ServerJoin.Register(this, OnJoin);
        }

        protected override void Dispose(bool Disposing)
        {
            if (Disposing)
            {
                ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
                ServerApi.Hooks.ServerJoin.Deregister(this, OnJoin);
            }
            base.Dispose(Disposing);
        }
        
        public void OnInitialize(EventArgs args)
        {
            // initialize database
            cidrbans = new CIDRBanManager();

            // adds commands
            Commands.ChatCommands.Add(new Command("cidrbans.use", CIDRBanCommand, "cidrban")
            {
                HelpDesc = new[]
                {
                    "{0}cidrban add <range> [reason] - Ban a CIDR range permanently.".SFormat(Commands.Specifier),
                    "{0}cidrban addtemp <range> <time> [reason] - Ban a CIDR range temporarily.".SFormat(Commands.Specifier),
                    "{0}cidrban del <ip/range> - Unban CIDR range or ranges that includes specified IP.".SFormat(Commands.Specifier),
                    "{0}cidrban list - List all CIDR ranges banned in the system.".SFormat(Commands.Specifier)
                }
            });
        }
        
        public void OnJoin(JoinEventArgs args)
        {
            if (args.Handled)
                return;
            if (!TShock.Config.EnableIPBans)
                return;
            
            // search a ban by player's IP
            TSPlayer player = TShock.Players[args.Who];
            CIDRBan ban = cidrbans.GetCIDRBanByIP(player.IP);
            if (ban == null)
                return;
            
            // parse expiration date
            DateTime exp;
            if (!DateTime.TryParse(ban.Expiration, out exp))
            {
                // no expiration date implies permaban
                player.Disconnect("You are banned forever: " + ban.Reason);
            }
            else
            {
                // remove a ban past the expiration date
                if (DateTime.UtcNow >= exp)
                {
                    cidrbans.DelCIDRBanByRange(ban.CIDR);
                    return;
                }

                // generate remaining ban time string for player
                TimeSpan ts = exp - DateTime.UtcNow;
                int months = ts.Days / 30;
                if (months > 0)
                {
                    player.Disconnect(String.Format("You are banned for {0} month{1} and {2} day{3}: {4}",
                        months, months == 1 ? "" : "s", ts.Days, ts.Days == 1 ? "" : "s", ban.Reason));
                }
                else if (ts.Days > 0)
                {
                    player.Disconnect(String.Format("You are banned for {0} day{1} and {2} hour{3}: {4}",
                        ts.Days, ts.Days == 1 ? "" : "s", ts.Hours, ts.Hours == 1 ? "" : "s", ban.Reason));
                }
                else if (ts.Hours > 0)
                {
                    player.Disconnect(String.Format("You are banned for {0} hour{1} and {2} minute{3}: {4}",
                        ts.Hours, ts.Hours == 1 ? "" : "s", ts.Minutes, ts.Minutes == 1 ? "" : "s", ban.Reason));
                }
                else if (ts.Minutes > 0)
                {
                    player.Disconnect(String.Format("You are banned for {0} minute{1} and {2} second{3}: {4}",
                        ts.Minutes, ts.Minutes == 1 ? "" : "s", ts.Seconds, ts.Seconds == 1 ? "" : "s", ban.Reason));
                }
                else
                {
                    player.Disconnect(String.Format("You are banned for {0} second{1}: {2}",
                        ts.Seconds, ts.Seconds == 1 ? "" : "s", ban.Reason));
                }
            }
            
            args.Handled = true;
        }
        
        private void CIDRBanCommand(CommandArgs args)
        {
            TSPlayer player = args.Player;

            // check subcommands, no subcommands imply "help"
            string subcmd = args.Parameters.Count == 0 ? "help" : args.Parameters[0].ToLower();
            switch (subcmd)
            {
                case "help":
                    {
                        player.SendInfoMessage("CIDR Bans Plugin");
                        player.SendInfoMessage("Description: Allows banning CIDR ranges");
                        player.SendInfoMessage("Syntax: {0}cidrban <add/addtemp/del/list> [arguments]", Commands.Specifier);
                        player.SendInfoMessage("Type {0}help cidrban for more info.", Commands.Specifier);
                    }
                    break;

                case "add":
                    {
                        // ensure proper usage
                        if (args.Parameters.Count < 2)
                        {
                            player.SendErrorMessage("Invalid syntax! Proper syntax: {0}cidrban add <range> [reason]", Commands.Specifier);
                            return;
                        }
                        if (!Regex.IsMatch(args.Parameters[1], rangeregex))
                        {
                            player.SendErrorMessage("Invalid CIDR range string! Proper format: 0-255.0-255.0-255.0-255/0-32");
                            return;
                        }

                        // parse reason string, set to default if none
                        if (args.Parameters.Count < 3)
                            args.Parameters.Add("Manually added IP address ban.");
                        args.Parameters[2] = String.Join(" ", args.Parameters.GetRange(2, args.Parameters.Count - 2));

                        // add ban to database
                        if (cidrbans.AddCIDRBan(args.Parameters[1], args.Parameters[2], player.Name, DateTime.UtcNow.ToString("s")))
                            player.SendSuccessMessage("Banned range {0} for '{1}'.", args.Parameters[1], args.Parameters[2]);
                        else
                            player.SendErrorMessage("Adding range {0} into database failed.", args.Parameters[1]);
                    }
                    break;

                case "addtemp":
                    {
                        // ensure proper usage
                        if (args.Parameters.Count < 3)
                        {
                            player.SendErrorMessage("Invalid syntax! Proper syntax: {0}cidrban addtemp <range> <time> [reason]", Commands.Specifier);
                            return;
                        }
                        if (!Regex.IsMatch(args.Parameters[1], rangeregex))
                        {
                            player.SendErrorMessage("Invalid CIDR range string! Proper format: 0-255.0-255.0-255.0-255/0-32");
                            return;
                        }

                        // parse time into seconds
                        int exp;
                        if (!TShock.Utils.TryParseTime(args.Parameters[2], out exp))
                        {
                            args.Player.SendErrorMessage("Invalid time string! Proper format: _d_h_m_s, with at least one time specifier.");
                            args.Player.SendErrorMessage("For example, 1d and 10h-30m+2m are both valid time strings, but 2 is not.");
                            return;
                        }

                        // parse reason string, set to default if none
                        if (args.Parameters.Count < 4)
                            args.Parameters.Add("Manually added IP address ban.");
                        args.Parameters[3] = String.Join(" ", args.Parameters.GetRange(3, args.Parameters.Count - 3));

                        // add ban to database
                        if (cidrbans.AddCIDRBan(args.Parameters[1], args.Parameters[3], player.Name,
                            DateTime.UtcNow.ToString("s"), DateTime.UtcNow.AddSeconds(exp).ToString("s")))
                            player.SendSuccessMessage("Banned range {0} for '{1}'.", args.Parameters[1], args.Parameters[3]);
                        else
                            player.SendErrorMessage("Adding range {0} into database failed.", args.Parameters[1]);
                    }
                    break;

                case "del":
                    {
                        // ensure proper usage
                        if (args.Parameters.Count < 2)
                        {
                            player.SendErrorMessage("Invalid syntax! Proper syntax: {0}cidrban del <ip/range>", Commands.Specifier);
                            return;
                        }

                        // remove ban via range
                        if (Regex.IsMatch(args.Parameters[1], rangeregex))
                        {
                            if (cidrbans.DelCIDRBanByRange(args.Parameters[1]))
                                player.SendSuccessMessage("Unbanned range {0}.", args.Parameters[1]);
                            else
                                player.SendErrorMessage("Removing range {0} from database failed.", args.Parameters[1]);
                            return;
                        }

                        // remove ban via ip
                        if (Regex.IsMatch(args.Parameters[1], ipregex))
                        {
                            // remove all ranges containing ip
                            List<string> removed = cidrbans.DelCIDRBanByIP(args.Parameters[1]);
                            if (removed.Count == 0)
                            {
                                player.SendErrorMessage("Removing range {0} from database failed.", args.Parameters[1]);
                                return;
                            }
                            player.SendSuccessMessage("Removed {0} range{1} from the database:", removed.Count, removed.Count == 1 ? "" : "s");
                            player.SendInfoMessage(String.Join(", ", removed));
                            return;
                        }

                        // improper argument format
                        player.SendErrorMessage("Invalid syntax! Proper syntax: {0}cidrban del <ip/range>", Commands.Specifier);
                        player.SendErrorMessage("IP proper format: 0-255.0-255.0-255.0-255");
                        player.SendErrorMessage("CIDR range proper format : 0-255.0-255.0-255.0-255/0-32");
                    }
                    break;

                case "list":
                    {
                        // integrate pagination tool
                        int pagenumber;
                        if (!PaginationTools.TryParsePageNumber(args.Parameters, 1, args.Player, out pagenumber))
                            return;

                        // fetch only ranges from the list
                        List<CIDRBan> list = cidrbans.GetCIDRBanList();
                        var namelist = from ban in list
                                       select ban.CIDR;

                        // show data from user's specified page
                        PaginationTools.SendPage(player, pagenumber, PaginationTools.BuildLinesFromTerms(namelist),
                                new PaginationTools.Settings
                                {
                                    HeaderFormat = "CIDR Range Bans ({0}/{1}):",
                                    FooterFormat = "Type {0}ban list {{0}} for more.".SFormat(Commands.Specifier),
                                    NothingToDisplayString = "There are currently no CIDR range bans."
                                });
                    }
                    break;
                
                default:
                    {
                        player.SendErrorMessage("Invalid subcommand. Type {0}help cidrban for information.", Commands.Specifier);
                    }
                    break;
            }
        }
    }
}
