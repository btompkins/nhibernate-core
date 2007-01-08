using System.Collections;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using log4net;
using NHibernate.Driver;
using NHibernate.SqlCommand;
using NHibernate.SqlTypes;

namespace NHibernate.JetDriver
{
	/// <summary>
	/// Implementation of IDriver for Jet database engine.
	/// Because of the weird JOIN clause syntax, this class has to translate the queries generated by NHibernate
	/// into the Jet syntax. This cannot be done anywhere else without having to heavily modify the logic of query creation.
	/// The translations of queries are cached.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Author: <a href="mailto:lukask@welldatatech.com">Lukas Krejci</a>
	/// </para>
	/// </remarks>
	public class JetDriver : OleDbDriver
	{
		private static ILog logger = LogManager.GetLogger(typeof(JetDriver));

		private IDictionary _queryCache = new Hashtable();

		public override IDbCommand GenerateCommand(CommandType type, SqlString sqlString, SqlType[] parameterTypes)
		{
			SqlString final;
			if (IsSelectStatement(sqlString))
			{
				final = FinalizeJoins(sqlString);
			}
			//else if(IsCreateOrAlterStatement(sqlString)) final = FinalizeDDL(sqlString);
			else
			{
				final = sqlString;
			}

			return base.GenerateCommand(type, final, parameterTypes);
		}

		/// <summary></summary>
		public override IDbConnection CreateConnection()
		{
			return new JetDbConnection();
		}

		/// <summary>
		/// We have to have a special db command type to support conversion of data types, because Access is weird.
		/// </summary>
		public override IDbCommand CreateCommand()
		{
			return new JetDbCommand();
		}

		/// <summary>
		/// MS Access expects @paramName
		/// </summary>
		public override bool UseNamedPrefixInParameter
		{
			get { return true; }
		}

		public override string NamedPrefix
		{
			get { return "@"; }
		}

		#region Query transformations

		/// <summary>
		///Jet engine has the following from clause syntax:
		///<code>
		///		tableexpression[, tableexpression]*
		///</code>
		///where tableexpression is:
		///<code>
		///		tablename [(INNER |LEFT | RIGHT) JOIN [(] tableexpression [)] ON ...]
		///</code>
		///where the parenthesises are necessary if the "inner" tableexpression is not just a single tablename.
		///Additionally INNER JOIN cannot be nested in LEFT | RIGHT JOIN.
		///To translate the simple non-parenthesized joins to the jet syntax, the following transformation must be done:
		///<code>
		///		A join B on ... join C on ... join D on ..., E join F on ... join G on ..., H join I on ..., J
		///has to be translated as:
		///		(select * from ((A join B on ...) join C on ...) join D on ...) as crazyAlias1, (select * from (E join F on ...) join G on ...) as crazyAlias2, (select * from H join I on ...) as crazyAlias3, J
		///</code>
		/// </summary>
		/// <param name="sqlString">the sqlstring to transform</param>
		/// <returns>sqlstring with parenthesized joins.</returns>
		private SqlString FinalizeJoins(SqlString sqlString)
		{
			if (_queryCache.Contains(sqlString))
			{
				return (SqlString) _queryCache[sqlString];
			}

			SqlStringBuilder beginning = new SqlStringBuilder(sqlString.Count);
			SqlStringBuilder end = new SqlStringBuilder(sqlString.Count);

			int beginOfFrom = sqlString.IndexOfCaseInsensitive("from");
			int endOfFrom = sqlString.IndexOfCaseInsensitive("where");

			if (beginOfFrom < 0)
			{
				return sqlString;
			}

			if (endOfFrom < 0)
			{
				endOfFrom = sqlString.Length;
			}

			string fromClause = sqlString.Substring(beginOfFrom, endOfFrom - beginOfFrom).ToString();
			
			string transformedFrom = TransformFromClause(fromClause);

			//put it all together again
			SqlStringBuilder final = new SqlStringBuilder(sqlString.Count + 1);
			final.Add(sqlString.Substring(0, beginOfFrom));
			final.Add(transformedFrom);
			final.Add(sqlString.Substring(endOfFrom));

			SqlString ret = final.ToSqlString();
			_queryCache[sqlString] = ret;

			return ret;
		}
		
		private string TransformFromClause(string fromClause)
		{
			string transformed;

			string[] blocks = fromClause.Split(',');
			if (blocks.Length > 1)
			{
				for (int i = 0; i < blocks.Length; i++)
				{
					string tr = TransformJoinBlock(blocks[i]);
					if (tr.IndexOf(" join ") > -1)
					{
						blocks[i] = "(select * from " + tr + ") as jetJoinAlias" + i.ToString();
					}
					else
					{
						blocks[i] = tr;
					}
				}

				transformed = string.Join(",", blocks);
			}
			else
			{
				transformed = TransformJoinBlock(blocks[0]);
			}

			return transformed;
		}

		/// <param name="block">A string representing one join block.</param>
		private string TransformJoinBlock(string block)
		{
			int parenthesisCount = 0;

			Regex re = new Regex(" join");
			string[] blockParts = re.Split(block);

			if (blockParts.Length > 1)
			{
				for (int i = 1; i < blockParts.Length; i++)
				{
					string part = blockParts[i];
					int parenthesisIndex = -1;

					if (part.EndsWith(" inner"))
					{
						parenthesisIndex = part.Length - 6;
					}
					else if (part.EndsWith(" left outer"))
					{
						parenthesisIndex = part.Length - 11;
					}
					else if (part.EndsWith(" right outer"))
					{
						parenthesisIndex = part.Length - 12;
					}

					if (parenthesisIndex == -1)
					{
						if (i < blockParts.Length - 1)
						{
							logger.Error("Invalid join syntax. Could not parenthesize the join block properly.");
							throw new QueryException("Invalid join syntax. Could not parenthesize the join block properly.");
						}

						//everything went ok. I'm processing the last block part and I've got no parenthesis to add.
						StringBuilder b = new StringBuilder(" ");
						for (int j = 0; j < parenthesisCount; j++)
						{
							b.Append("(");
						}
						b.Append(string.Join(" join", blockParts));

						return b.ToString();
					}
					else
					{
						parenthesisCount++;
						blockParts[i] = part.Insert(parenthesisIndex, ")");
					}
				}

				//the last block part contained the join. This should not happen.
				logger.Error("Invalid join syntax. Could not parenthesize the join block properly.");
				throw new QueryException("Invalid join syntax. Could not parenthesize the join block properly.");
			}
			else
			{
				return blockParts[0];
			}
		}

		private bool IsSelectStatement(SqlString sqlString)
		{
			return sqlString.StartsWithCaseInsensitive("select");
		}

		#endregion
	}
}
