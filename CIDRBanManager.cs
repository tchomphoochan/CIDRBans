using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.IO;

using TShockAPI;
using TShockAPI.DB;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;

namespace CIDRBans
{
    /// <summary>Connection to CIDRBans database</summary>
    public class CIDRBanManager
    {
        // database initialization
        private IDbConnection db;
        public CIDRBanManager()
        {
            switch (TShock.Config.StorageType.ToLower())
            {
                case "mysql":
                    string[] host = TShock.Config.MySqlHost.Split(':');
                    db = new MySqlConnection()
                    {
                        ConnectionString = String.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                        host[0],
                        host.Length == 1 ? "3306" : host[1],
                        TShock.Config.MySqlDbName,
                        TShock.Config.MySqlUsername,
                        TShock.Config.MySqlPassword)
                    };
                    break;
                case "sqlite":
                    string dbPath = Path.Combine(TShock.SavePath, "CIDRBans.sqlite");
                    db = new SqliteConnection(String.Format("uri=file://{0},Version=3", dbPath));
                    break;
            }
            SqlTableCreator creator = new SqlTableCreator(db, db.GetSqlType() == SqlType.Sqlite ? 
                (IQueryBuilder)new SqliteQueryCreator() : new MysqlQueryCreator());
                creator.EnsureTableStructure(new SqlTable("CIDRBans",
                    new SqlColumn("CIDR", MySqlDbType.String) { Primary = true, Length = 20 },
                    new SqlColumn("Reason", MySqlDbType.Text),
                    new SqlColumn("BanningUser", MySqlDbType.Text),
                    new SqlColumn("Date", MySqlDbType.Text),
                    new SqlColumn("Expiration", MySqlDbType.Text)));
        }

        /// <summary>Search CIDR bans with given IP</summary>
        /// <param name="IP">IP string for searching</param>
        /// <returns>First CIDRBan object found</returns>
        public CIDRBan GetCIDRBanByIP(string check)
        {
            try
            {
                // search for matching range in database
                List<CIDRBan> banlist = GetCIDRBanList();
                foreach (CIDRBan ban in banlist)
                {
                    if (CIDRBan.Check(check, ban.CIDR))
                        return ban;
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error(ex.ToString());
            }

            return null;
        }

        /// <summary>Search for all CIDR ranges in database</summary>
        /// <returns>List of all CIDRBan objects found</returns>
        public List<CIDRBan> GetCIDRBanList()
        {
            try
            {
                // list all rows in database
                List<CIDRBan> banlist = new List<CIDRBan>();
                using (var reader = db.QueryReader("SELECT * FROM CIDRBans"))
                {
                    while (reader.Read())
                    {
                        banlist.Add(new CIDRBan(reader.Get<string>("CIDR"), reader.Get<string>("Reason"),
                            reader.Get<string>("BanningUser"), reader.Get<string>("Date"), reader.Get<string>("Expiration")));
                    }
                }
                return banlist;
            }
            catch (Exception ex)
            {
                TShock.Log.Error(ex.ToString());
            }

            return new List<CIDRBan>();
        }

        /// <summary>Add a CIDR range to database</summary>
        /// <returns>Success</returns>
        public bool AddCIDRBan(string cidr, string reason = "", string user = "", string date = "", string expire = "")
        {
            try
            {
                return db.Query("INSERT INTO CIDRBans (CIDR, Reason, BanningUser, Date, Expiration) " +
                    "VALUES (@0, @1, @2, @3, @4)", cidr, reason, user, date, expire) != 0;
            }
            catch (Exception ex)
            {
                TShock.Log.Error(ex.ToString());
            }

            return false;
        }

        /// <summary>Delete specified CIDR range from database</summary>
        /// <returns>Success</returns>
        public bool DelCIDRBanByRange(string cidr)
        {
            try
            {
                return db.Query("DELETE FROM CIDRBans WHERE CIDR = @0", cidr) != 0;
            }
            catch (Exception ex)
            {
                TShock.Log.Error(ex.ToString());
            }

            return false;
        }

        /// <summary>Delete all CIDR ranges matched up with an IP from database</summary>
        /// <returns>List of CIDR ranges removed from database</returns>
        public List<string> DelCIDRBanByIP(string ip)
        {
            try
            {
                // check all delete canditates
                List<CIDRBan> banlist = GetCIDRBanList();
                List<string> removelist = new List<string>();
                foreach (CIDRBan ban in banlist)
                {
                    if (CIDRBan.Check(ip, ban.CIDR))
                        removelist.Add(ban.CIDR);
                }

                // remove canditates from database
                foreach (string removed in removelist)
                    db.Query("DELETE FROM CIDRBans WHERE CIDR = @0", removed);

                return removelist;
            }
            catch (Exception ex)
            {
                TShock.Log.Error(ex.ToString());
            }

            return new List<string>();
        }
    }

    /// <summary>Row information from CIDRBans table</summary>
    public class CIDRBan
    {
        // columns
        public string CIDR { get; set; }
        public string Reason { get; set; }
        public string BanningUser { get; set; }
        public string Date { get; set; }
        public string Expiration { get; set; }
        
        // class constructors
        public CIDRBan(string cidr, string reason, string user, string date, string expire)
        {
            this.CIDR = cidr;
            this.Reason = reason;
            this.BanningUser = user;
            this.Date = date;
            this.Expiration = expire;
        }
        
        public CIDRBan()
        {
            this.CIDR = "";
            this.Reason = "";
            this.BanningUser = "";
            this.Date = "";
            this.Expiration = "";
        }

        /// <summary>Check if IP is included in CIDR Range or not</summary>
        /// <returns>Match</returns>
        public static bool Check(string check, string cidr)
        {
            // create the mask supposed to match
            const uint defaultmask = 0xffffffff;
            int maskcount = Convert.ToInt32(cidr.Split('/')[1]);
            uint mask = Convert.ToUInt32(defaultmask << (32 - maskcount));

            // translate cidr range to 32-bit integer, ignore all non-matching parts
            List<byte> cidrbyteparts = (from num in cidr.Split('/')[0].Split('.')
                                        select Convert.ToByte(num)).ToList();
            uint cidrip = (((uint)cidrbyteparts[0] * (uint)Math.Pow(2, 24)) + ((uint)cidrbyteparts[1] * (uint)Math.Pow(2, 16)) +
                ((uint)cidrbyteparts[2] * (uint)Math.Pow(2, 8)) + (uint)cidrbyteparts[3]);
            cidrip &= mask;

            // translate ip range to 32-bit integer, ignore all non-matching parts
            List<byte> checkbyteparts = (from num in check.Split('.')
                                         select Convert.ToByte(num)).ToList();
            uint checkip = (((uint)checkbyteparts[0] * (uint)Math.Pow(2, 24)) + ((uint)checkbyteparts[1] * (uint)Math.Pow(2, 16)) +
                ((uint)checkbyteparts[2] * (uint)Math.Pow(2, 8)) + (uint)checkbyteparts[3]);
            checkip &= mask;

            // match masked ip/range
            if (cidrip == checkip)
                return true;
            else
                return false;
        }
    }
}
