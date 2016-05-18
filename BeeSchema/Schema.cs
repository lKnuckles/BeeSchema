﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace BeeSchema {
	public class Schema {
		Lexer lexer;
		string directory;

		Dictionary<string, Node> types;
		Node root;

		static string[] control = { "if", "unless", /*"else", "elif",*/ "while", "until"/*, "for"*/ };

		Schema() {
			types = new Dictionary<string, Node>();
		}

		public static Schema FromFile(string filename) {
			var r = new Schema();
			var file = File.OpenText(filename);
			r.lexer = new Lexer(file);
			r.directory = Path.GetDirectoryName(filename);
			r.Read();
			file.Close();

			return r;
		}

		public static Schema FromText(string text) {
			var r = new Schema();
			r.lexer = new Lexer(new StringReader(text));
			r.Read();

			return r;
		}

		public OrderedDictionary Parse(string filename) {
			var s = File.OpenRead(filename);

			return Parse(s);
		}

		public OrderedDictionary Parse(byte[] data) {
			var s = new MemoryStream(data);

			return Parse(s);
		}

		public OrderedDictionary Parse(Stream stream) {
			var b = new BinaryReader(stream, Encoding.ASCII);
			var r = Parse(b, root.Children);
			b.Close();

			return r;
		}

		OrderedDictionary Parse(BinaryReader reader, List<Node> nodes) {
			var r = new OrderedDictionary();

			foreach (var n in nodes)
				Parse(reader, n, r);

			return r;
		}

		void Parse(BinaryReader reader, Node node, OrderedDictionary scope) {
			var r = new Result();
			r.Type = node.Type;
			r.Name = node.Name;
			r.Position = reader.BaseStream.Position;
			r.Comment = node.Comment;

			r.Value = ParsePrimitive(reader, r.Type, out r.Size);

			r.TypeName = GetTypeName(node);

			if (r.Value != null) {
				scope.Add(r.Name, r);
				return;
			}

			switch (r.Type) {
				case NodeType.Struct: {
						var tnode = types[r.TypeName];
						var val = Parse(reader, tnode.Children);
						r.Value = val;

						foreach (Result v in val.Values)
							r.Size += v.Size;

						scope.Add(r.Name, r);
					}
					break;
				case NodeType.Enum: {
						var tnode = types[r.TypeName];
						var type = tnode.Children[0].Type;
						var val = Convert.ToInt64(ParsePrimitive(reader, type, out r.Size));

						foreach (var c in tnode.Children) {
							if (Convert.ToInt64(c.Value) == val) {
								r.Value = c.Name;
								break;
							}
						}

						scope.Add(r.Name, r);
					}
					break;
				case NodeType.Bitfield: {
						var tnode = types[(string)node.Value];
						var type = tnode.Children[0].Type;
						var val = Convert.ToInt64(ParsePrimitive(reader, type, out r.Size));

						var list = new OrderedDictionary();

						foreach (var n in tnode.Children) {
							var result = new Result();
							result.Name = n.Name;
							result.Position = r.Position;
							result.Size = r.Size;
							var bits = Convert.ToInt32(n.Value);
							var mask = (1 << bits) - 1;
							result.Value = val & mask;
							val >>= bits;
							list.Add(result.Name, result);
						}

						r.Value = list;
						scope.Add(r.Name, r);
					}
					break;
				case NodeType.Array: {
						var tnode = node.Children[0];
						var lnode = node.Children[1];
						var dt = new DataTable();
						var sb = new StringBuilder();

						var expr = BuildExpression(reader, lnode.Children, scope);

						var length = Convert.ToInt32(dt.Compute(expr, ""));

						if (tnode.Type == NodeType.Char) {
							var a = reader.ReadChars(length);
							r.Value = new string(a);
							r.Size = length;
						}
						else {
							var list = new OrderedDictionary();

							for (int i = 0; i < length; i++) {
								var nt = new Node();
								nt.Type = tnode.Type;
								nt.Value = tnode.Value;
								nt.Name = $"{node.Name}[{i}]";
								Parse(reader, nt, list);
							}

							foreach (Result l in list.Values)
								r.Size += l.Size;

							r.Value = list;
						}

						scope.Add(r.Name, r);
					}
					break;
				case NodeType.IfCond:
				case NodeType.UnlessCond: {
						var cnode = node.Children[0];
						var bnode = node.Children[1];

						var dt = new DataTable();
						var expr = BuildExpression(reader, cnode.Children, scope);
						var cond = (bool)dt.Compute(expr, "");
						cond = (node.Type == NodeType.UnlessCond) ? !cond : cond;

						if (cond) {
							var body = Parse(reader, bnode.Children);

							foreach (Result v in body.Values)
								scope.Add(v.Name, v);
						}
					}
					break;
				case NodeType.WhileLoop:
				case NodeType.UntilLoop: {
						var cnode = node.Children[0];
						var bnode = node.Children[1];

						int i = 0;
						var dt = new DataTable();

						while (true) {
							var expr = BuildExpression(reader, cnode.Children, scope);
							var cond = (bool)dt.Compute(expr, "");
							cond = (node.Type == NodeType.UntilLoop) ? !cond : cond;

							if (cond) {
								var body = Parse(reader, bnode.Children);

								foreach (Result v in body.Values) {
									v.Name += $"[{i++}]";
									scope.Add(v.Name, v);
								}
							}
							else
								break;
						}
					}
					break;
			}
		}

		string GetTypeName(Node node) {
			if (node.Type == NodeType.Struct
				|| node.Type == NodeType.Enum
				|| node.Type == NodeType.Bitfield)
				return (string)node.Value;
			else if (node.Type == NodeType.Array) {
				var tnode = node.Children[0];
				return $"{GetTypeName(tnode)}[]";
			}
			else
				return node.Type.ToString().ToLower();
		}

		string BuildExpression(BinaryReader reader, List<Node> nodes, OrderedDictionary scope) {
			var sb = new StringBuilder();

			foreach (var n in nodes) {
				switch (n.Type) {
					case NodeType.Long:
						sb.Append((long)n.Value);
						break;
					case NodeType.AddOper:
						sb.Append('+');
						break;
					case NodeType.SubOper:
						sb.Append('-');
						break;
					case NodeType.MulOper:
						sb.Append('*');
						break;
					case NodeType.DivOper:
						sb.Append('/');
						break;
					case NodeType.EofMacro:
						sb.Append($"({reader.BaseStream.Position}>={reader.BaseStream.Length})");
						break;
					case NodeType.PosMacro:
						sb.Append($"{reader.BaseStream.Position}");
						break;
					case NodeType.SizeMacro:
						sb.Append($"{reader.BaseStream.Length}");
						break;
					case NodeType.String: {
							var val = (string)n.Value;

							if (val.Contains('.')) {
								var tnode = types[val.Substring(0, val.IndexOf('.'))];

								foreach (var c in tnode.Children) {
									if (c.Name == val.Substring(val.IndexOf('.') + 1)) {
										sb.Append(Convert.ToInt64(c.Value));
										break;
									}
								}
							}
							else {
								var result = (Result)scope[(string)n.Value];

								if (result.Type == NodeType.Enum) {
									var tnode = types[result.TypeName];

									foreach (var c in tnode.Children) {
										if (c.Name == (string)result.Value) {
											sb.Append(Convert.ToInt64(c.Value));
											break;
										}
									}
								}
								else
									sb.Append(Convert.ToInt64(result.Value));
							}
						}
						break;
					case NodeType.NotComp:
						sb.Append(" NOT ");
						break;
					case NodeType.EqualComp:
						sb.Append("=");
						break;
					case NodeType.NEqualComp:
						sb.Append("!=");
						break;
					case NodeType.GreaterComp:
						sb.Append(">");
						break;
					case NodeType.LessComp:
						sb.Append("<");
						break;
					case NodeType.GoEComp:
						sb.Append(">=");
						break;
					case NodeType.LoEComp:
						sb.Append("<=");
						break;
				}
			}

			return sb.ToString();
		}

		object ParsePrimitive(BinaryReader reader, NodeType type, out int size) {
			if (type > NodeType.Epoch) {
				size = 0;
				return null;
			}

			switch (type) {
				case NodeType.Bool:
					size = 1;
					return reader.ReadBoolean();
				case NodeType.Byte:
					size = 1;
					return reader.ReadByte();
				case NodeType.SByte:
					size = 1;
					return reader.ReadSByte();
				case NodeType.UShort:
					size = 2;
					return reader.ReadUInt16();
				case NodeType.Short:
					size = 2;
					return reader.ReadInt16();
				case NodeType.UInt:
					size = 4;
					return reader.ReadUInt32();
				case NodeType.Int:
					size = 4;
					return reader.ReadInt32();
				case NodeType.ULong:
					size = 8;
					return reader.ReadUInt64();
				case NodeType.Long:
					size = 8;
					return reader.ReadInt64();
				case NodeType.Float:
					size = 4;
					return reader.ReadSingle();
				case NodeType.Double:
					size = 8;
					return reader.ReadDouble();
				case NodeType.Char:
					size = 1;
					return reader.ReadChar();
				case NodeType.String: {
						var sb = new StringBuilder();
						int c;

						while ((c = reader.Read()) != '\0')
							sb.Append((char)c);

						size = sb.Length;
						return sb.ToString();
					}
				case NodeType.IPAddress:
					size = 4;
					return new IPAddress(reader.ReadBytes(4));
				case NodeType.Epoch: {
						var ts = reader.ReadUInt32();
						var t = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
						size = 4;
						return t.AddSeconds(ts);
					}
			}

			throw new Exception("We should never reach this...");
		}

		void Read(bool readSchemaBlock = true) {
			Token token;

			while ((token = lexer.NextToken()) != null) {
				if (token.Type == TokenType.LineComment
					|| token.Type == TokenType.BlockComment)
					continue;

				if (token.Type != TokenType.Word)
					throw new Exception($"({token.Line}:{token.Column}) Unexpected token.");

				var node = new Node();

				if (token.Value == "include") {
					node = null;
					ReadInclude();
				}
				else if (token.Value == "struct") {
					node.Type = NodeType.StructDef;
					ReadStructBlock(node);
				}
				else if (token.Value == "enum") {
					node.Type = NodeType.EnumDef;
					ReadEnumBlock(node);
				}
				else if (token.Value == "bitfield") {
					node.Type = NodeType.BitfieldDef;
					ReadBitfieldBlock(node);
				}
				else if (token.Value == "schema") {
					node.Type = NodeType.SchemaDef;

					if (readSchemaBlock) {
						ReadSchemaBlock(node);
						root = node;
					}
				}
				else
					throw new Exception($"({token.Line}:{token.Column}) Unexpected identifier: Expected [include, struct, enum, bitfield, schema]. Got [{token.Value}].");
			}
		}

		void ReadInclude() {
			var token = lexer.NextToken();

			if (token.Type != TokenType.Word)
				throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

			var filename = token.Value;
			filename = $"{((directory != "") ? $"{directory}/" : "")}{filename}";

			if (!File.Exists(filename))
				throw new Exception($"({token.Line}:{token.Column}) Couldn't locate include file: {filename}");

			token = lexer.NextToken();

			if (token.Type != TokenType.Semicolon)
				throw new Exception($"({token.Line}:{token.Column}) Expected [;]");

			var oldLexer = lexer;
			lexer = new Lexer(File.OpenText($"{filename}"));
			Read(false);
			lexer = oldLexer;
		}

		void ReadStructBlock(Node node) {
			var token = lexer.NextToken();

			if (token.Type != TokenType.Word)
				throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

			node.Name = token.Value;

			ReadBody(node);
			types.Add(node.Name, node);
		}

		void ReadBody(Node node) {
			var token = lexer.NextToken();

			if (token.Type != TokenType.OpenBrace)
				throw new Exception($"({token.Line}:{token.Column}) Expected [{{].");

			Node child = null;

			while ((token = lexer.NextToken()).Type != TokenType.CloseBrace) {
				if ((token.Type == TokenType.LineComment || token.Type == TokenType.BlockComment)
					&& child != null) {
					child.Comment = token.Value;
					child = null;
					continue;
				}

				var idents = new List<string>();

				if (token.Type != TokenType.Word)
					throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

				if (control.Contains(token.Value)) {
					child = new Node();
					child.Name = $"({token.Line}:{token.Column})";

					if (token.Value == "if")
						child.Type = NodeType.IfCond;
					else if (token.Value == "unless")
						child.Type = NodeType.UnlessCond;
					/*else if (token.Value == "else")
						child.Type = NodeType.ElseCond;
					else if (token.Value == "elif")
						child.Type = NodeType.ElifCond;*/
					else if (token.Value == "while")
						child.Type = NodeType.WhileLoop;
					else if (token.Value == "until")
						child.Type = NodeType.UntilLoop;
					/*else if (token.Value == "for")
						child.Type = NodeType.ForLoop;*/

					//if (token.Value != "else") {
					var cond = new Node();
					cond.Name = "condition";
					token = lexer.NextToken();

					if (token.Type != TokenType.OpenParen)
						throw new Exception($"({token.Line}:{token.Column}) Expected [(].");

					while ((token = lexer.NextToken()).Type != TokenType.CloseParen) {
						var condChild = new Node();
						condChild.Name = $"({token.Line}:{token.Column})";

						switch (token.Type) {
							case TokenType.Number:
								condChild.Type = NodeType.Long;
								condChild.Value = long.Parse(token.Value);
								break;
							case TokenType.Equal:
								condChild.Type = NodeType.EqualComp;
								break;
							case TokenType.NotEqual:
								condChild.Type = NodeType.NEqualComp;
								break;
							case TokenType.Less:
								condChild.Type = NodeType.LessComp;
								break;
							case TokenType.Greater:
								condChild.Type = NodeType.GreaterComp;
								break;
							case TokenType.LessOrEqual:
								condChild.Type = NodeType.LoEComp;
								break;
							case TokenType.GreaterOrEqual:
								condChild.Type = NodeType.GoEComp;
								break;
							case TokenType.Not:
								condChild.Type = NodeType.NotComp;
								break;
							case TokenType.Plus:
								condChild.Type = NodeType.AddOper;
								break;
							case TokenType.Minus:
								condChild.Type = NodeType.SubOper;
								break;
							case TokenType.Asterisk:
								condChild.Type = NodeType.MulOper;
								break;
							case TokenType.Divide:
								condChild.Type = NodeType.DivOper;
								break;
							case TokenType.Word:
								if (token.Value.StartsWith("@")) {
									if (token.Value == "@eof")
										condChild.Type = NodeType.EofMacro;
									else if (token.Value == "@size")
										condChild.Type = NodeType.SizeMacro;
									else if (token.Value == "@pos")
										condChild.Type = NodeType.PosMacro;
									else
										throw new Exception($"({token.Line}:{token.Column}) Unknown macro.");

									break;
								}
								condChild.Type = NodeType.String;
								condChild.Value = token.Value;
								break;
							default:
								throw new Exception($"({token.Line}:{token.Column}) Unexpected token.");
						}

						cond.Children.Add(condChild);
					}

					if (cond.Children.Count == 0)
						throw new Exception($"({token.Line}:{token.Column}) Control statements must specify a condition.");

					child.Children.Add(cond);
					//}

					var body = new Node();
					body.Name = "body";
					ReadBody(body);
					child.Children.Add(body);
					node.Children.Add(child);

					continue;
				}

				while (true) {
					idents.Add(token.Value);
					token = lexer.NextToken();

					if (token.Type == TokenType.Comma) {
						token = lexer.NextToken();

						if (token.Type != TokenType.Word)
							throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

						continue;
					}
					else if (token.Type == TokenType.Colon)
						break;
					else
						throw new Exception($"({token.Line}:{token.Column}) Unexpected token.");
				}

				token = lexer.NextToken();

				if (token.Type != TokenType.Word)
					throw new Exception($"({token.Line}:{token.Column}) Expected type identifier.");

				NodeType type, subType = 0;
				type = GetTypeFromString(token.Value);
				string userType = null;
				Node arrayType = null;
				Node arrayLength = null;

				if (type == NodeType.Error)
					throw new Exception($"({token.Line}:{token.Column}) The type [{token.Value}] does not exist.");

				if (type == NodeType.Struct
					|| type == NodeType.Enum
					|| type == NodeType.Bitfield) {
					userType = token.Value;
				}

				token = lexer.NextToken();

				/*if (token.Type == TokenType.Asterisk) {
					subType = type;
					type = NodeType.Pointer;
					token = lexer.NextToken();
				}
				else*/
				if (token.Type == TokenType.OpenBracket) {
					arrayType = new Node();
					arrayType.Type = type;
					arrayType.Name = "type";
					type = NodeType.Array;
					arrayLength = new Node();
					arrayLength.Type = NodeType.Operation;
					arrayLength.Name = "length";

					if (arrayType.Type == NodeType.Struct
						|| arrayType.Type == NodeType.Enum
						|| arrayType.Type == NodeType.Bitfield)
						arrayType.Value = userType;

					while ((token = lexer.NextToken()).Type != TokenType.CloseBracket) {
						var alChild = new Node();
						alChild.Name = $"({token.Line}:{token.Column})";

						switch (token.Type) {
							case TokenType.Number:
								alChild.Type = NodeType.Long;
								alChild.Value = long.Parse(token.Value);
								break;
							case TokenType.Asterisk:
								alChild.Type = NodeType.MulOper;
								break;
							case TokenType.Divide:
								alChild.Type = NodeType.DivOper;
								break;
							case TokenType.Plus:
								alChild.Type = NodeType.AddOper;
								break;
							case TokenType.Minus:
								alChild.Type = NodeType.SubOper;
								break;
							case TokenType.Word:
								if (token.Value.StartsWith("@")) {
									if (token.Value == "@eof")
										alChild.Type = NodeType.EofMacro;
									else if (token.Value == "@size")
										alChild.Type = NodeType.SizeMacro;
									else if (token.Value == "@pos")
										alChild.Type = NodeType.PosMacro;
									else
										throw new Exception($"({token.Line}:{token.Column}) Unknown macro.");

									break;
								}

								alChild.Type = NodeType.String;
								alChild.Value = token.Value;
								break;
							default:
								throw new Exception($"({token.Line}:{token.Column}) Unexpected token.");
						}

						arrayLength.Children.Add(alChild);
					}

					token = lexer.NextToken();
				}
				else if (token.Type != TokenType.Semicolon)
					throw new Exception($"({token.Line}:{token.Column}) Expected [;].");

				foreach (var id in idents) {
					child = new Node();
					child.Name = id;
					child.Type = type;

					/*if (child.Type == NodeType.Pointer)
						child.Value = subType;
					else*/
					if (child.Type == NodeType.Array) {
						child.Children.Add(arrayType);
						child.Children.Add(arrayLength);
					}
					else if (child.Type == NodeType.Struct
							  || child.Type == NodeType.Enum
							  || child.Type == NodeType.Bitfield) {
						child.Value = userType;
					}

					node.Children.Add(child);
				}
			}
		}

		void ReadEnumBlock(Node node) {
			var token = lexer.NextToken();

			if (token.Type != TokenType.Word)
				throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

			node.Name = token.Value;

			token = lexer.NextToken();

			if (token.Type != TokenType.Colon)
				throw new Exception($"({token.Line}:{token.Column}) Enum declarations must declare a base type.");

			token = lexer.NextToken();

			if (token.Type != TokenType.Word)
				throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

			NodeType type = GetPrimitiveTypeFromString(token.Value);

			if (type == NodeType.Error)
				throw new Exception($"({token.Line}:{token.Column}) Only primitive types are supported as base types.");

			token = lexer.NextToken();

			if (token.Type != TokenType.OpenBrace)
				throw new Exception($"({token.Line}:{token.Column}) Expected [{{].");

			var val = 0L;
			token = lexer.NextToken();

			while (token.Type != TokenType.CloseBrace) {
				var child = new Node();
				child.Type = type;

				if (token.Type != TokenType.Word)
					throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

				child.Name = token.Value;

				token = lexer.NextToken();

				if (token.Type == TokenType.Assignment) {
					token = lexer.NextToken();

					if (token.Type != TokenType.Number)
						throw new Exception($"({token.Line}:{token.Column}) Expected numerical value.");

					if (token.Value.StartsWith("0x"))
						val = long.Parse(token.Value.Substring(2), NumberStyles.HexNumber);
					else
						val = long.Parse(token.Value);

					token = lexer.NextToken();
				}

				child.Value = val++;

				if (token.Type != TokenType.Comma && token.Type != TokenType.CloseBrace)
					throw new Exception($"({token.Line}:{token.Column}) Expected [,] or [}}].");

				if (token.Type == TokenType.Comma)
					token = lexer.NextToken();

				node.Children.Add(child);
			}

			types.Add(node.Name, node);
		}

		void ReadBitfieldBlock(Node node) {
			var token = lexer.NextToken();

			if (token.Type != TokenType.Word)
				throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

			node.Name = token.Value;

			token = lexer.NextToken();

			if (token.Type != TokenType.Colon)
				throw new Exception($"({token.Line}:{token.Column}) Bitfield declarations must declare a base type.");

			token = lexer.NextToken();

			if (token.Type != TokenType.Word)
				throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

			NodeType type = GetPrimitiveTypeFromString(token.Value);

			if (type == NodeType.Error)
				throw new Exception($"({token.Line}:{token.Column}) Only primitive types are supported as base types.");

			token = lexer.NextToken();

			if (token.Type != TokenType.OpenBrace)
				throw new Exception($"({token.Line}:{token.Column}) Expected [{{].");

			Node child = null;

			while ((token = lexer.NextToken()).Type != TokenType.CloseBrace) {
				if ((token.Type == TokenType.LineComment || token.Type == TokenType.BlockComment)
					&& child != null) {
					child.Comment = token.Value;
					child = null;
					continue;
				}

				child = new Node();
				child.Type = type;

				if (token.Type != TokenType.Word)
					throw new Exception($"({token.Line}:{token.Column}) Expected identifier.");

				child.Name = token.Value;

				token = lexer.NextToken();

				if (token.Type != TokenType.Colon)
					throw new Exception($"({token.Line}:{token.Column}) A size in bits must be specified for bitfield members.");

				token = lexer.NextToken();

				if (token.Type != TokenType.Number)
					throw new Exception($"({token.Line}:{token.Column}) Expected numerical value.");

				child.Value = long.Parse(token.Value);

				token = lexer.NextToken();

				if (token.Type != TokenType.Semicolon)
					throw new Exception($"({token.Line}:{token.Column}) Expected [;].");

				node.Children.Add(child);
			}

			types.Add(node.Name, node);
		}

		void ReadSchemaBlock(Node node) {
			ReadBody(node);
		}

		NodeType GetTypeFromString(string type) {
			var prim = GetPrimitiveTypeFromString(type);

			if (prim != NodeType.Error)
				return prim;

			if (types.Count == 0 || !types.ContainsKey(type))
				return NodeType.Error;

			var node = (Node)types[type];

			switch (node.Type) {
				case NodeType.StructDef:
					return NodeType.Struct;
				case NodeType.EnumDef:
					return NodeType.Enum;
				case NodeType.BitfieldDef:
					return NodeType.Bitfield;
			}

			return NodeType.Error;
		}

		NodeType GetPrimitiveTypeFromString(string type) {
			var r = NodeType.Error;

			if (Enum.TryParse(type, true, out r))
				return r;

			return NodeType.Error;
		}
	}
}