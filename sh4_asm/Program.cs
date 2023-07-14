using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Globalization;

namespace sh4_asm
{
    public class Program
    {
        enum ParseType
        {
            none,
            name,
            integer_number,
            float_number,
            hex_number,
            label_declaration,
            register_direct,
            fr_register_direct,
            fv_register_direct,
            dr_register_direct,
            xd_register_direct,
            r_bank_register_direct,
            register_indirect,
            register_indirect_post_increment,
            register_indirect_pre_decrement,
            register_indirect_displacement,
            register_indexed_indirect,
            gbr_register,
            gbr_indirect_displacement,
            gbr_indirect_indexed,
            pc_displacement, // relies on inner_token for number
            pc_register,
            other_register,
            string_data,
            absolute_displacement_address,
            expression
        }

        // 1 statement per line is made up of several tokens
        class Token
        {
            public string raw_string;
            public ParseType parse_type;
            public Token inner_token;
            // too many special cases for it to make sense conceptually to treat this like an array
            public Token inner_token2;
            public Expression expression;
            public long value;
            public uint size;
            public bool is_value_assigned;
        }

        class Statement
        {
            public char[] raw_line;
            public string instruction; // either like "mov.b" or "#data" or "#symbol"
            public List<Token> tokens;
            public int line_number;
            public uint address;
            public string module;
            public List<Symbol> associated_labels;
            public int repeat_count;
        }

        enum SymbolType
        {
            none,
            label,
            from_symbol_directive, // #symbol
            builtin,
            instruction,
            register,
            alias
        }

        enum RegisterType
        {
            not_applicable,
            r,
            fr,
            dr,
            xd,
            fv,
            pc,
            gbr,
            r_bank,
            other
        }

        class Symbol
        {
            public string name;
            public string short_name; // without module in it
            public string alias_target;
            public long value;
            public SymbolType symbol_type;
            public RegisterType register_type;
            public int line_number;
            public int statement_number;
            public uint address;
            public uint size;
            public string module;
            public bool has_been_associated;
        }


        class Module
        {
            public string name;
            public int statement_number_offset;
        }

        class Expression
        {
            public List<string> subtokens;
            public List<SubtokenType> subtokens_type;

            public enum SubtokenType
            {
                add,
                subtract,
                multiply,
                divide,
                open_parenthesis,
                close_parenthesis,
                name,
                decimal_number,
                hex_number,
            }
        }

        static List<Expression> expression_table;
        static Dictionary<string, Symbol> symbol_table;

        static uint starting_offset;
        static Dictionary<string, Module> modules_loaded;

        static string working_directory;

        enum Endian {
            Little,
            Big
        }

        static Endian endian;

        static void Main(string[] args)
        {
            endian = Endian.Little;
            starting_offset = 0xce30000;

            if (args.Length < 2)
            {
                Console.WriteLine("need input filename, output filename.\nthird option is offset, is optional");
                return;
            }

            init_symbols();

            string input_file = null;
            string output_file = null;
            string logfile = null;

            {
                Queue<string> arg_queue = new Queue<string>(args.Length);

                foreach (string s in args)
                {
                    arg_queue.Enqueue(s);
                }

                bool changed_starting_offset = false;

                while (arg_queue.Count > 0)
                {
                    string arg = arg_queue.Dequeue();

                    switch (arg)
                    {
                        case "--symboltable":
                            if (arg_queue.Count > 0)
                            {
                                if (!load_symbol_table(arg_queue.Dequeue()))
                                {
                                    Console.WriteLine("Error reading symbol table, terminating");
                                    return;
                                }
                            }
                            else
                            {
                                Console.WriteLine("--symboltable needs a filename after");
                                return;
                            }
                            break;
                        default:
                            if (input_file == null)
                            {
                                input_file = arg;
                                break;
                            }

                            if (output_file == null)
                            {
                                output_file = arg;
                                break;
                            }

                            try
                            {
                                if (!changed_starting_offset)
                                {
                                    string arg_upper = arg.ToUpperInvariant();

                                    // fixme error properly on invalid offset
                                    if (arg_upper.StartsWith("0X"))
                                    {
                                        starting_offset = Convert.ToUInt32(arg_upper, 16);
                                    }
                                    else
                                    {
                                        starting_offset = UInt32.Parse(arg_upper, CultureInfo.InvariantCulture);
                                    }

                                    changed_starting_offset = true;

                                    break;
                                }
                            }
                            catch (FormatException _e)
                            {

                            }

                            if (logfile == null)
                            {
                                logfile = arg;
                            }
                            else
                            {
                                Console.WriteLine("unknown parameter: " + arg);
                                return;
                            }
                            break;
                    }
                }
            }

            if (input_file == null || output_file == null)
            {
                Console.WriteLine("need input filename, output filename.\nthird option is offset, is optional");
                return;
            }

            working_directory = Path.GetDirectoryName(Path.GetFullPath(input_file));

            Console.WriteLine("asm using offset " + starting_offset.ToString("X2"));

            modules_loaded = new Dictionary<string, Module>();
            Module main_module = new Module();
            main_module.name = Path.GetFileNameWithoutExtension(input_file).ToUpperInvariant();
            main_module.statement_number_offset = 0;

            modules_loaded.Add(main_module.name, main_module);

            List<Statement> statements;
            using (StreamReader reader = File.OpenText(input_file))
            {
                statements = tokenize_and_parse(reader, main_module.name, 0);
            }

            {
                List<Statement> new_statements = new List<Statement>(statements.Count * 2);
                handle_module_loading(statements, new_statements, 0);
                statements = new_statements;
            }

            fix_associated_labels(statements);

            intermediate_step(statements);

            string temp_filename = Path.GetTempFileName();
            using (BinaryWriter writer = new BinaryWriter(new MemoryStream()))
            {
                if (logfile != null)
                {
                    Console.WriteLine("writing asm output to " + logfile);

                    string log_temp_filename = Path.GetTempFileName();
                    using (StreamWriter log_writer = new StreamWriter(File.Open(log_temp_filename, FileMode.Create)))
                    {
                        code_generation(statements, writer, log_writer);
                    }

                    File.Delete(logfile + ".bak");

                    if (File.Exists(logfile))
                    {
                        File.Copy(logfile, logfile + ".bak");
                    }

                    File.Delete(logfile);
                    File.Copy(log_temp_filename, logfile);
                }
                else
                {
                    code_generation(statements, writer);
                }

                using (FileStream out_stream = File.Open(temp_filename, FileMode.Create))
                {
                    writer.Flush();
                    ((MemoryStream)(writer.BaseStream)).WriteTo(out_stream);
                    out_stream.Flush();
                }
            }

            File.Delete(output_file + ".bak");

            if (File.Exists(output_file))
            {
                File.Copy(output_file, output_file + ".bak");
            }

            File.Delete(output_file);
            File.Copy(temp_filename, output_file);

            //output_labels_for_unlabeled(statements);
            //output_ranges_that_might_be_code(statements);
            
            save_symbol_table(
                statements,
                Path.Combine(working_directory,
                    Path.GetFileNameWithoutExtension(input_file) + ".symbol_table"));
        } // main

        static bool load_symbol_table(string filename)
        {
            if (Path.GetExtension(filename).ToLowerInvariant() == ".asm")
            {
                throw new NotImplementedException();
            }
            else
            {
                using (BinaryReader reader = new BinaryReader(File.Open(filename, FileMode.Open)))
                {
                    return load_symbol_table_binary(reader);
                }
            }
        }

        private static bool load_symbol_table_binary(BinaryReader reader)
        {
            if (reader.ReadUInt32() != 0x4C424154)
            {
                Console.WriteLine("expected 'TABL' at start of binary symbol table file");
                return false;
            }

            {
                long version = reader.ReadInt64();
                if (version != 1)
                {
                    Console.WriteLine("only supports symbol table version 1, file is version " + version);
                    return false;
                }
            }

            int module_count = reader.ReadInt32();

            List<string> modules = new List<string>(module_count);

            for (int i = 0; i < module_count; i++)
            {
                modules.Add(reader.ReadString());

                //Console.WriteLine("module: " + modules[i]);
            }

            int symbol_count = reader.ReadInt32();

            //Console.WriteLine(symbol_count);

            StringBuilder sb = new StringBuilder(255);

            for (int i = 0; i < symbol_count; i++)
            {
                int module_index;

                if (module_count <= byte.MaxValue)
                {
                    module_index = reader.ReadByte();
                }
                else if (module_count <= UInt16.MaxValue)
                {
                    module_index = reader.ReadUInt16();
                }
                else
                {
                    module_index = reader.ReadInt32();
                }

                if (module_index > module_count)
                {
                    Console.WriteLine("module index " + module_index + " is out of range");
                    return false;
                }

                sb.Clear();
                sb.Append(modules[module_index]);
                sb.Append(".");
                sb.Append(reader.ReadString());

                string symbol_name = sb.ToString();
                uint symbol_value = reader.ReadUInt32();

                Symbol l = new Symbol();
                l.module = modules[module_index].ToLowerInvariant();
                l.name = symbol_name.ToLowerInvariant();
                l.address = symbol_value;

                if (symbol_table.ContainsKey(l.name))
                {
                    Console.WriteLine("duplicate: " + symbol_name + " with value " + symbol_value.ToString("X2"));
                }
                
                symbol_table.Add(l.name, l);
            }

            return true;
        }

        static void save_symbol_table(List<Statement> statements, string filename)
        {
            string temp_filename = Path.GetTempFileName();
            using (BinaryWriter writer = new BinaryWriter(File.Open(temp_filename, FileMode.Create)))
            {
                generate_symbol_table_to_binary_stream(statements, writer);
            }

            File.Delete(filename + ".bak");

            if (File.Exists(filename))
            {
                File.Copy(filename, filename + ".bak");
            }

            File.Delete(filename);
            File.Copy(temp_filename, filename);
        }

        static void generate_symbol_table_to_text_stream(List<Statement> statements, StreamWriter writer)
        {
            foreach (Symbol symbol in symbol_table.Values)
            {
                // TODO non label symbols?
                if (symbol.symbol_type == SymbolType.label)
                {
                    writer.Write("#symbol ");
                    writer.Write(symbol.name.ToLowerInvariant());
                    writer.Write(" 0x");
                    writer.Write(symbol.value.ToString("X2"));
                    writer.WriteLine();
                }
            }
        }

        static void generate_symbol_table_to_binary_stream(List<Statement> statements, BinaryWriter writer)
        {
            Dictionary<string, int> module_indices
                = new Dictionary<string, int>(modules_loaded.Count);

            writer.Write("TABL".ToCharArray());
            writer.Write((long)01);

            int modules_count_max = modules_loaded.Count;
            
            writer.Write(modules_loaded.Count);

            int index = 0;
            foreach (Module module in modules_loaded.Values)
            {
                writer.Write(module.name);
                module_indices.Add(module.name, index);
                index++;
            }

            int symbol_count = 0;
            foreach (Symbol symbol in symbol_table.Values)
            {
                // TODO non label symbols?
                if (symbol.symbol_type == SymbolType.label)
                {
                    symbol_count++;
                }
            }
            writer.Write(symbol_count);

            foreach (Symbol symbol in symbol_table.Values)
            {
                // TODO non label symbols?
                if (symbol.symbol_type == SymbolType.label)
                {
                    if (modules_count_max <= byte.MaxValue)
                    {
                        writer.Write((byte)module_indices[symbol.module]);
                    }
                    else if (modules_count_max <= UInt16.MaxValue)
                    {
                        writer.Write((UInt16)module_indices[symbol.module]);
                    }
                    else
                    {
                        writer.Write(module_indices[symbol.module]);
                    }

                    writer.Write(symbol.short_name);
                    writer.Write((UInt32)symbol.value);
                }
            }
        }

        static void output_ranges_that_might_be_code(List<Statement> statements)
        {
            HashSet<uint> addresses = new HashSet<uint>();

            foreach (Statement statement in statements)
            {
                long potential_address = get_address_if_jump(statement);

                if (potential_address >= 0)
                {
                    addresses.Add(unchecked((uint)potential_address));
                }
            }

            bool bLookingForDataJumpedTo = true;
            uint start_address = 0;
            foreach (Statement statement in statements)
            {
                if (bLookingForDataJumpedTo)
                {
                    if (addresses.Contains(statement.address)
                        && statement.instruction == "#DATA")
                    {
                        start_address = statement.address;
                        bLookingForDataJumpedTo = false;
                    }
                }
                else
                {
                    if (symbol_table[statement.instruction].symbol_type == SymbolType.instruction)
                    {
                        bLookingForDataJumpedTo = true;
                        Console.WriteLine("code?? " + start_address.ToString("X2") + " to " + statement.address.ToString("X2"));
                    }
                }
            }
        }

        static long get_address_if_jump(Statement statement)
        {
            switch (statement.instruction)
            {
                case "BF":
                case "BF/S":
                case "BF.S":
                case "BRA":
                case "BSR":
                case "BT":
                case "BT.S":
                case "BT/S":
                    return statement.tokens[0].value;
                default:
                    return -1;
            }
            return -1;
        }

        static void output_labels_for_unlabeled(List<Statement> statements)
        {
            foreach (Statement statement in statements)
            {
                long addr = get_address_if_raw(statement);

                if (addr > 0)
                {
                    Console.WriteLine("note, for line " + statement.line_number + " (" + statement.module + "):");

                    Console.WriteLine(statement.raw_line);
                    //Console.WriteLine(" ; loc_" + addr.ToString("X2").ToLowerInvariant());

                    for (int i = 0; i < statements.Count; i++)
                    {
                        if (statements[i].address == addr)
                        {
                            if (statements[i].instruction != "#ALIGN4"
                                && statements[i].instruction != "#ALIGN"
                                && statements[i].instruction != "#ALIGN4_NOP"
                                && statements[i].instruction != "#ALIGN16"
                                && statements[i].instruction != "#ALIGN16_NOP")
                            {
                                Console.WriteLine(" can add loc_" +
                                    addr.ToString("X2").ToLowerInvariant()
                                    + ": before line " + statements[i].line_number + " of " + statements[i].module);
                                Console.WriteLine(statements[i].raw_line);

                                break;
                            }
                        }

                        if (statements[i].address > addr)
                        {
                            Console.WriteLine(statements[i].address.ToString("X2").ToLowerInvariant()
                                + " is greater than expected addr of " 
                                + addr.ToString("X2").ToLowerInvariant());
                            Console.WriteLine(" can add loc_" +
                                addr.ToString("X2").ToLowerInvariant()
                                + ": near??/before??? line "
                                + statements[i].line_number
                                + " of " + statements[i].module);
                            Console.WriteLine(statements[i].raw_line);
                            Console.WriteLine("(note: this may be because the previous line is larger than 2 bytes or something else?)");
                            Console.WriteLine("previous line:");
                            Console.WriteLine(statements[i-1].raw_line);
                            break;
                        }
                    }

                    Console.WriteLine();
                }
            }
        }

        static long get_address_if_raw(Statement statement)
        {
            switch (statement.instruction)
            {
                case "BF":
                    if (check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return calculate_pc_displacement(statement, 2, -256, 254) * 2 + statement.address + 4;
                    }
                    break;
                case "BF/S":
                case "BF.S":
                    if (check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return calculate_pc_displacement(statement, 2, -256, 254) * 2 + statement.address + 4;
                    }
                    break;
                case "BRA":
                    if (check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return calculate_pc_displacement(statement, 2, -4096, 4094) * 2 + statement.address + 4;
                    }
                    break;
                case "BSR":
                    if (check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return calculate_pc_displacement(statement, 2, -4096, 4094) * 2 + statement.address + 4;
                    }
                    break;
                case "BT":
                    if (check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return calculate_pc_displacement(statement, 2, -256, 254) * 2 + statement.address + 4;
                    }
                    break;
                case "BT.S":
                case "BT/S":
                    if (check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return calculate_pc_displacement(statement, 2, -256, 254) * 2 + statement.address + 4;
                    }
                    break;
                case "MOVA":
                    if (check_arguments(statement, ParseType.pc_displacement, ParseType.register_direct)
                        && statement.tokens[0].inner_token.parse_type != ParseType.name)
                    {
                        return (calculate_pc_displacement(statement, 4, -256, 254) * 4 + statement.address + 4) & 0xFFFF_FFFC;
                    }
                    break;
                case "MOV.W":
                    if (check_arguments(statement, ParseType.pc_displacement, ParseType.register_direct)
                        && statement.tokens[0].inner_token.parse_type != ParseType.name)
                    {
                        return calculate_pc_displacement(statement, 2, -256, 254) * 2 + statement.address + 4;
                    }
                    break;
                case "MOV.L":
                    if (check_arguments(statement, ParseType.pc_displacement, ParseType.register_direct)
                        && statement.tokens[0].inner_token.parse_type != ParseType.name)
                    {
                        return (calculate_pc_displacement(statement, 4, -256, 254) * 4 + statement.address + 4) & 0xFFFF_FFFC;
                    }
                    break;
                default:
                    return -1;
            }
            return -1;
        }

        // actually converts internal data structures into raw bytes to feed into the writer
        static void code_generation(List<Statement> statements, BinaryWriter writer, TextWriter log_writer = null)
        {
            long saved_position = 0;
            Dictionary<uint,List<Symbol>> label_addresses = new Dictionary<uint, List<Symbol>>();
            if (log_writer != null)
            {
                foreach (KeyValuePair<string, Symbol> pair in symbol_table)
                {
                    if (pair.Value.symbol_type == SymbolType.label)
                    {
                        if (!label_addresses.ContainsKey(pair.Value.address))
                        {
                            label_addresses.Add(pair.Value.address, new List<Symbol>());
                        }
                        label_addresses[pair.Value.address].Add(pair.Value);
                    }
                }
            }

            foreach (Statement statement in statements)
            {
                if (log_writer != null)
                {
                    log_writer.WriteLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
                    log_writer.Write("; line number: ");
                    log_writer.Write(statement.line_number);
                    log_writer.Write("\n; at 'memory' address ");
                    log_writer.Write(statement.address.ToString("X4"));
                    log_writer.Write(", file output address ");
                    log_writer.Write(writer.BaseStream.Position.ToString("X4"));
                    log_writer.Write(", module: ");
                    log_writer.Write(statement.module);
                    log_writer.Write("\n");
                    if (label_addresses.ContainsKey(statement.address))
                    {
                        log_writer.Write(";labels:\n");
                        
                        foreach (Symbol l in label_addresses[statement.address])
                        {
                            log_writer.Write("\t");
                            log_writer.Write(l.name);
                            log_writer.Write(":\n");
                        }
                    }
                    log_writer.Write(";input line:\n;  ");
                    log_writer.Write(statement.raw_line);
                    log_writer.Write("\n; tokenized:\n");
                    log_writer.Write("\t");
                    log_writer.Write(statement.instruction);
                    log_writer.Write(" ");
                    foreach (Token t in statement.tokens)
                    {
                        log_writer.Write(t.raw_string);
                        log_writer.Write(" ");
                    }
                    log_writer.WriteLine();

                    log_writer.Write("; argument values: ");
                    foreach (Token t in statement.tokens)
                    {
                        log_writer.Write(t.value);
                        if (t.inner_token != null)
                        {
                            log_writer.Write("(");
                            log_writer.Write(t.inner_token.value.ToString("X2"));
                            if (t.inner_token2 != null)
                            {
                                log_writer.Write(",");
                                log_writer.Write(t.inner_token2.value.ToString("X2"));
                            }
                            log_writer.Write(")");
                        }
                        log_writer.Write(" ");
                    }

                    log_writer.WriteLine();

                    saved_position = writer.BaseStream.Position;

                }

                for (int i = 0; i < statement.repeat_count; i++)
                {
                    generate_statement(statement, writer);
                }

                if (log_writer != null)
                {
                    log_writer.Write("; actual output: ");

                    using (MemoryStream rstream = new MemoryStream())
                    {
                        writer.BaseStream.Flush();
                        ((MemoryStream)writer.BaseStream).WriteTo(rstream);
                        rstream.Flush();

                        using (BinaryReader reader = new BinaryReader(rstream))
                        {
                            //Console.WriteLine((int)(writer.BaseStream.Position - saved_position));
                            reader.BaseStream.Seek(saved_position, SeekOrigin.Begin);

                            byte[] real_output = reader.ReadBytes((int)(writer.BaseStream.Position - saved_position));
                            //Console.WriteLine((int)(writer.BaseStream.Position - saved_position));
                            foreach (byte b in real_output)
                            {
                                //Console.WriteLine(b);
                                log_writer.Write(b.ToString("X2"));
                                log_writer.Write(" ");
                            }

                            log_writer.WriteLine();
                            log_writer.WriteLine(";;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;");
                            log_writer.WriteLine();
                            log_writer.WriteLine();
                        }//using reader
                    }//using rstream
                } // if log_writer != null
            }
        }

        // converts individual instruction into bytes and writes it into the writer
        static void generate_statement(Statement statement, BinaryWriter writer)
        {
            if (statement.instruction == "#DATA"
                || statement.instruction == "#DATA8"
                || statement.instruction == "#DATA16")
            {
                generate_data(statement, writer);
                return;
            }
            else if (statement.instruction == "#ALIGN4" || statement.instruction == "#ALIGN16")
            {
                uint align_size = 4;

                if (statement.instruction == "#ALIGN16")
                {
                    align_size = 16;
                }
                uint alignment = statement.address % align_size;


                uint align_fixed = 0;
                while (alignment > 0)
                {
                    writer.Write((byte)00);
                    align_fixed++;
                    alignment = (statement.address + align_fixed) % align_size;
                }
                return;
            }
            else if (statement.instruction == "#ALIGN4_NOP" || statement.instruction == "#ALIGN16_NOP")
            {
                uint align_size = 4;

                if (statement.instruction == "#ALIGN16_NOP")
                {
                    align_size = 16;
                }

                uint alignment = statement.address % align_size;

                if (alignment % 2 == 1)
                {
                    Error(
                            statement.raw_line, statement.module, statement.line_number, -1,
                            statement.instruction + " must be 2-aligned already."
                        );
                }

                uint align_fixed = 0;
                while (alignment > 0)
                {
                    if (endian == Endian.Little)
                    {
                        writer.Write((byte)09);
                        writer.Write((byte)00);
                    }
                    else
                    {
                        writer.Write((byte)00);
                        writer.Write((byte)09);
                    }
                    align_fixed += 2;
                    alignment = (statement.address + align_fixed) % align_size;
                }
                return;
            }
            else if (statement.instruction == "#ALIGN")
            {
                uint alignment = statement.address % 2;
                uint align_fixed = 0;
                while (alignment > 0)
                {
                    writer.Write((byte)00);
                    align_fixed++;
                    alignment = (statement.address + align_fixed) % 2;
                }
                return;
            }
            else if (statement.instruction == "#IMPORT_RAW_DATA")
            {
                generate_import_raw_data(statement, writer);
                return;
            }
            else if (statement.instruction == "#BIG_ENDIAN")
            {
                endian = Endian.Big;
                return;
            }
            else if (statement.instruction == "#LITTLE_ENDIAN")
            {
                endian = Endian.Little;
                return;
            }
            else if (statement.instruction.StartsWith("#"))
            {
                if (!(symbol_table.ContainsKey(statement.instruction)
                      && symbol_table[statement.instruction].symbol_type == SymbolType.builtin
                    ))
                {
                    Error(
                            statement.raw_line, statement.module, statement.line_number, -1,
                            "unknown directive " + statement.instruction
                        );
                }
                return;
            }

            ushort output = generate_instruction(statement);


            if (endian == Endian.Big)
            {
                output = (ushort)(((output >> 8) & 0xFFFF) | (output << 8));
                //Console.WriteLine("line: " + statement.line_number + ":  " + output.ToString("X4"));
            }

            writer.Write(output);
        } // generate_statement

        static void generate_import_raw_data(Statement statement, BinaryWriter writer)
        {
            if (statement.tokens != null && statement.tokens.Count == 1)
            {
                Token t = statement.tokens[0];
                if (t.parse_type == ParseType.string_data)
                {
                    string filename = Path.Combine(working_directory, t.raw_string);

                    if (File.Exists(filename))
                    {
                        long size = new FileInfo(filename).Length;

                        if (size != t.value)
                        {
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                filename + " changed size during compilation?");
                        }
                        else
                        {
                            using (BinaryReader reader = new BinaryReader(File.Open(filename, FileMode.Open)))
                            {
                                for (int i = 0; i < statement.repeat_count; i++)
                                {
                                    writer.Write(reader.ReadBytes((int)size));

                                    reader.BaseStream.Seek(0, SeekOrigin.Begin);
                                }
                            }
                        }
                    }
                    else
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                                "raw data file " + filename + " doesn't exist");
                    }
                }
                else
                {
                    Error(statement.raw_line, statement.module, statement.line_number, -1,
                        t.raw_string + " is a symbol that exists, but not of the right type to use for #data");
                }
            }
            else
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "wrong number of inputs for " + statement.instruction + " directive, need a single filename");
            }
        }

        static void generate_data(Statement statement, BinaryWriter writer)
        {
            uint address = statement.address;
            foreach (Token t in statement.tokens)
            {
                if (t.parse_type == ParseType.string_data)
                {
                    writer.Write(Encoding.ASCII.GetBytes(t.raw_string));

                    address += t.size;
                }
                else
                {
                    if (endian == Endian.Little)
                    {
                        switch (t.size)
                        {
                            case 1:
                                writer.Write((byte)t.value);
                                break;
                            case 2:
                                writer.Write((short)t.value);
                                break;
                            case 4:
                                writer.Write((Int32)t.value);
                                break;
                            case 8:
                                writer.Write((Int64)t.value);
                                break;
                            default:
                                Error(
                                    statement.raw_line, statement.module, statement.line_number, -1,
                                    "Data chunk sizes other than 1, 2, 4, or 8 bytes not currently supported. (\"" + t.raw_string + "\" is " + t.size + " bytes)"
                                    );
                                break;
                        }
                    }
                    else
                    {
                        switch (t.size)
                        {
                            case 1:
                                writer.Write((byte)t.value);
                                break;
                            case 2:
                                UInt16 u16 = unchecked((UInt16)(t.value));
                                u16 = (UInt16)(
                                    ((u16 & 0xFF) << 8)
                                    | ((u16 & 0xFF00) >> 8)
                                    );

                                writer.Write(u16);
                                break;
                            case 4:
                                UInt32 u32 = unchecked((UInt32)t.value);
                                u32 = (UInt32)(
                                    ((u32 & 0xFF) << 24)
                                    | ((u32 & 0xFF00) << 8)
                                    | ((u32 & 0xFF0000) >> 8)
                                    | ((u32 & 0xFF000000) >> 24)
                                    );

                                writer.Write(u32);
                                break;
                            case 8:
                                UInt64 u64 = unchecked((UInt64)t.value);
                                u64 = (UInt64)(
                                      ((u64 & 0x00000000_000000FF) << 56)
                                    | ((u64 & 0x00000000_0000FF00) << 40)
                                    | ((u64 & 0x00000000_00FF0000) << 24)
                                    | ((u64 & 0x00000000_FF000000) << 8)

                                    | ((u64 & 0x000000FF_00000000) >> 8)
                                    | ((u64 & 0x0000FF00_00000000) >> 24)
                                    | ((u64 & 0x00FF0000_00000000) >> 40)
                                    | ((u64 & 0xFF000000_00000000) >> 56)
                                    );
                                
                                writer.Write(u64);
                                break;
                            default:
                                Error(
                                    statement.raw_line, statement.module, statement.line_number, -1,
                                    "Data chunk sizes other than 1, 2, 4, or 8 bytes not currently supported. (\"" + t.raw_string + "\" is " + t.size + " bytes)"
                                    );
                                break;
                        }
                    }

                    // i was really hoping this would be a true assumption but it's not unfortunately, after testing
                    /*
                    if (t.parse_type == ParseType.name
                        && symbol_table[t.raw_string.ToUpperInvariant()].symbol_type == SymbolType.label
                        && t.value % 4 != 0)
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                                "Data chunk from label \"" + t.raw_string + "\" is not aligned to 4, is aligned to "
                                + (t.value % 4) + " instead. (address : 0x" + t.value.ToString("X8") + ")" 
                                + "\ntry adding some NOPs or 1byte data to pad?"
                                );
                    }
                    */

                    if (address % t.size != 0)
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                                "Data chunk size is " + t.size + ", but address (0x"
                                + statement.address.ToString("X4") + ") is not aligned to that size. off by "
                                + (address % t.size) + " (\"" + t.raw_string + "\" is " + t.size + " bytes)."
                                + "\ntry adding some NOPs or 1byte data to pad?"
                                );
                    }

                    address += t.size;
                }
            }
        }

        static ushort generate_instruction(Statement statement)
        {
            // for the sake of legibility
            List<Token> tokens = statement.tokens;

            switch (statement.instruction)
            {
                case "ADD":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_1100, statement);
                    }
                    else if (check_arguments(statement, ParseType.integer_number, ParseType.register_direct))
                    {
                        return generate_immediate_register(0b0111, statement);
                    }
                    break;
                case "ADDC":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_1110, statement);
                    }
                    break;
                case "ADDV":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_1111, statement);
                    }
                    break;
                case "AND":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1001, statement);
                    }
                    else if (check_arguments(statement, ParseType.integer_number, ParseType.register_direct))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1001, statement);
                    }
                    break;
                case "AND.B":
                    if (check_arguments(statement, ParseType.integer_number, ParseType.gbr_indirect_indexed))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1101, statement);
                    }
                    break;
                case "BF":
                    if (check_arguments(statement, ParseType.integer_number)
                        || check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return generate_pc_displacement8(0b1000_1011, statement);
                    }
                    break;
                case "BF/S":
                case "BF.S":
                    if (check_arguments(statement, ParseType.integer_number)
                        || check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return generate_pc_displacement8(0b1000_1111, statement);
                    }
                    break;
                case "BRA":
                    if (check_arguments(statement, ParseType.integer_number)
                        || check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return generate_pc_displacement12(0b1010, statement);
                    }
                    break;
                case "BRAF":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0000_0000_0010_0011, statement, 0);
                    }
                    break;
                case "BSR":
                    if (check_arguments(statement, ParseType.integer_number)
                        || check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return generate_pc_displacement12(0b1011, statement);
                    }
                    break;
                case "BSRF":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0000_0000_0000_0011, statement, 0);
                    }
                    break;
                case "BT":
                    if (check_arguments(statement, ParseType.integer_number)
                        || check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return generate_pc_displacement8(0b1000_1001, statement);
                    }
                    break;
                case "BT.S":
                case "BT/S":
                    if (check_arguments(statement, ParseType.integer_number)
                        || check_arguments(statement, ParseType.absolute_displacement_address))
                    {
                        return generate_pc_displacement8(0b1000_1101, statement);
                    }
                    break;
                case "CLRMAC":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000101000;
                    }
                    break;
                case "CLRS":
                    if (check_arguments(statement))
                    {
                        return 0b0000000001001000;
                    }
                    break;
                case "CLRT":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000001000;
                    }
                    break;
                case "CMP/EQ":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_0000, statement);
                    }
                    else if (check_arguments(statement, ParseType.integer_number, ParseType.register_direct))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1000_1000, statement);
                    }
                    break;
                case "CMP/GE":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_0011, statement);
                    }
                    break;
                case "CMP/GT":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_0111, statement);
                    }
                    break;
                case "CMP/HI":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_0110, statement);
                    }
                    break;
                case "CMP/HS":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_0010, statement);
                    }
                    break;
                case "CMP/PL":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0001_0101, statement, 0);
                    }
                    break;
                case "CMP/PZ":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0001_0001, statement, 0);
                    }
                    break;
                case "CMP/STR":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1100, statement);
                    }
                    break;
                case "DIV0S":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_0111, statement);
                    }
                    break;
                case "DIV0U":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000011001;
                    }
                    break;
                case "DIV1":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_0100, statement);
                    }
                    break;
                case "DMULS.L":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_1101, statement);
                    }
                    break;
                case "DMULU.L":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_0101, statement);
                    }
                    break;
                case "DT":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0001_0000, statement, 0);
                    }
                    break;
                case "EXTS.B":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1110, statement);
                    }
                    break;
                case "EXTS.W":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1111, statement);
                    }
                    break;
                case "EXTU.B":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1100, statement);
                    }
                    break;
                case "EXTU.W":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1101, statement);
                    }
                    break;
                case "FABS":
                    if (check_arguments(statement, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct))
                    {
                        return generate_register(0b1111_0000_0101_1101, statement, 0);
                    }
                    break;
                case "FADD":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct, ParseType.dr_register_direct))
                    {
                        return generate_register_register_swapped(0b1111_0000_0000_0000, statement);
                    }
                    break;
                case "FCMP/EQ":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct, ParseType.dr_register_direct))
                    {
                        return generate_register_register_swapped(0b1111_0000_0000_0100, statement);
                    }
                    break;
                case "FCMP/GT":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct, ParseType.dr_register_direct))
                    {
                        return generate_register_register_swapped(0b1111_0000_0000_0101, statement);
                    }
                    break;
                case "FCNVDS":
                    if (check_arguments(statement, ParseType.dr_register_direct, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b1111_0000_1011_1101, statement, 0);
                    }
                    break;
                case "FCNVSD":
                    if (check_arguments(statement, ParseType.other_register, ParseType.dr_register_direct)
                        && tokens[0].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b1111_0000_1010_1101, statement, 1);
                    }
                    break;
                case "FDIV":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct, ParseType.dr_register_direct))
                    {
                        return generate_register_register_swapped(0b1111_0000_0000_0011, statement);
                    }
                    break;
                case "FIPR":
                    if (check_arguments(statement, ParseType.fv_register_direct, ParseType.fv_register_direct))
                    {
                        return generate_fv_register_register(0b1111_0000_1110_1101, statement);
                    }
                    break;
                case "FLDI0":
                    if (check_arguments(statement, ParseType.fr_register_direct))
                    {
                        return generate_register(0b1111_0000_1000_1101, statement, 0);
                    }
                    break;
                case "FLDI1":
                    if (check_arguments(statement, ParseType.fr_register_direct))
                    {
                        return generate_register(0b1111_0000_1001_1101, statement, 0);
                    }
                    break;
                case "FLDS":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b1111_0000_0001_1101, statement, 0);
                    }
                    break;
                case "FLOAT":
                    if (
                        (
                        check_arguments(statement, ParseType.other_register, ParseType.fr_register_direct)
                        ||
                        check_arguments(statement, ParseType.other_register, ParseType.dr_register_direct)
                        )

                        && tokens[0].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b1111_0000_0010_1101, statement, 1);
                    }
                    break;
                case "FMAC":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct, ParseType.fr_register_direct))
                    {
                        check_error_require_register_zero(statement, 0, "FR0");
                        return generate_register_register_swapped(0b1111_0000_0000_1110, statement, 1);
                    }
                    break;
                case "FMOV":
                case "FMOV.S":
                    {
                        if (check_arguments(statement, ParseType.xd_register_direct, ParseType.register_indirect))
                        {
                            return generate_register_register_swapped(0b1111_0000_0001_1010, statement);
                        }
                        else if (check_arguments(statement, ParseType.register_indirect, ParseType.xd_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0001_0000_1000, statement);
                        }
                        else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.xd_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0001_0000_1001, statement);
                        }
                        else if (check_arguments(statement, ParseType.xd_register_direct, ParseType.register_indirect_pre_decrement))
                        {
                            return generate_register_register_swapped(0b1111_0000_0001_1011, statement);
                        }
                        else if (check_arguments(statement, ParseType.register_indexed_indirect, ParseType.xd_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0001_0000_0110, statement);
                        }
                        else if (check_arguments(statement, ParseType.xd_register_direct, ParseType.register_indexed_indirect))
                        {
                            return generate_register_register_swapped(0b1111_0000_0001_0111, statement);
                        }
                        else if (check_arguments(statement, ParseType.xd_register_direct, ParseType.xd_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0001_0001_1100, statement);
                        }
                        else if (check_arguments(statement, ParseType.xd_register_direct, ParseType.dr_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0000_0001_1100, statement);
                        }
                        else if (check_arguments(statement, ParseType.dr_register_direct, ParseType.xd_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0001_0000_1100, statement);
                        }
                        else if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct)
                            || check_arguments(statement, ParseType.dr_register_direct, ParseType.dr_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0000_0000_1100, statement);
                        }
                        else if (check_arguments(statement, ParseType.fr_register_direct, ParseType.register_indirect)
                            || check_arguments(statement, ParseType.dr_register_direct, ParseType.register_indirect))
                        {
                            return generate_register_register_swapped(0b1111_0000_0000_1010, statement);
                        }
                        else if (check_arguments(statement, ParseType.register_indirect, ParseType.fr_register_direct)
                            || check_arguments(statement, ParseType.register_indirect, ParseType.dr_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0000_0000_1000, statement);
                        }
                        else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.fr_register_direct)
                            || check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.dr_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0000_0000_1001, statement);
                        }
                        else if (check_arguments(statement, ParseType.fr_register_direct, ParseType.register_indirect_pre_decrement)
                            || check_arguments(statement, ParseType.dr_register_direct, ParseType.register_indirect_pre_decrement))
                        {
                            return generate_register_register_swapped(0b1111_0000_0000_1011, statement);
                        }
                        else if (check_arguments(statement, ParseType.register_indexed_indirect, ParseType.fr_register_direct)
                            || check_arguments(statement, ParseType.register_indexed_indirect, ParseType.dr_register_direct))
                        {
                            return generate_register_register_swapped(0b1111_0000_0000_0110, statement);
                        }
                        else if (check_arguments(statement, ParseType.fr_register_direct, ParseType.register_indexed_indirect)
                            || check_arguments(statement, ParseType.dr_register_direct, ParseType.register_indexed_indirect))
                        {
                            return generate_register_register_swapped(0b1111_0000_0000_0111, statement);
                        }
                    }
                    break;
                case "FMUL":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct, ParseType.dr_register_direct))
                    {
                        return generate_register_register_swapped(0b1111_0000_0000_0010, statement);
                    }
                    break;
                case "FNEG":
                    if (check_arguments(statement, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct))
                    {
                        return generate_register(0b1111_0000_0100_1101, statement, 0);
                    }
                    break;
                case "FSCA":
                    if (
                        (
                        check_arguments(statement, ParseType.other_register, ParseType.fr_register_direct)
                        ||
                        check_arguments(statement, ParseType.other_register, ParseType.dr_register_direct)
                        )

                        && tokens[0].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b1111_0000_1111_1101, statement, 1);
                    }
                    break;
                case "FRCHG":
                    if (check_arguments(statement))
                    {
                        return 0b1111101111111101;
                    }
                    break;
                case "FSCHG":
                    if (check_arguments(statement))
                    {
                        return 0b1111001111111101;
                    }
                    break;
                case "FSQRT":
                    if (check_arguments(statement, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct))
                    {
                        return generate_register(0b1111_0000_0110_1101, statement, 0);
                    }
                    break;
                case "FSRRA":
                    if (check_arguments(statement, ParseType.fr_register_direct)
                        ||
                        check_arguments(statement, ParseType.dr_register_direct))
                    {
                        return generate_register(0b1111_0000_0111_1101, statement, 0);
                    }
                    break;
                case "FSTS":
                    if (
                        (
                        check_arguments(statement, ParseType.other_register, ParseType.fr_register_direct)
                        ||
                        check_arguments(statement, ParseType.other_register, ParseType.dr_register_direct)
                        )

                        && tokens[0].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b1111_0000_0000_1101, statement, 1);
                    }
                    break;
                case "FSUB":
                    if (check_arguments(statement, ParseType.fr_register_direct, ParseType.fr_register_direct)
                        || check_arguments(statement, ParseType.dr_register_direct, ParseType.dr_register_direct))
                    {
                        return generate_register_register_swapped(0b1111_0000_0000_0001, statement);
                    }
                    break;
                case "FTRC":
                    if (
                            (
                                check_arguments(statement, ParseType.dr_register_direct, ParseType.other_register)
                                || check_arguments(statement, ParseType.fr_register_direct, ParseType.other_register)
                            )
                            && tokens[1].raw_string.ToUpperInvariant() == "FPUL"
                        )
                    {
                        return generate_register(0b1111_0000_0011_1101, statement, 0);
                    }
                    break;
                case "FTRV":
                    if (check_arguments(statement, ParseType.other_register, ParseType.fv_register_direct)
                        && tokens[0].raw_string.ToUpperInvariant() == "XMTRX")
                    {
                        return generate_fv_register(0b1111_0001_1111_1101, statement, 1);
                    }
                    break;
                case "JMP":
                    if (check_arguments(statement, ParseType.register_indirect))
                    {
                        return generate_register(0b0100_0000_0010_1011, statement, 0);
                    }
                    break;
                case "JSR":
                    if (check_arguments(statement, ParseType.register_indirect))
                    {
                        return generate_register(0b0100_0000_0000_1011, statement, 0);
                    }
                    break;
                case "LDC":
                case "LDC.L":
                    {
                        ushort ender = 0b1110;

                        if (statement.instruction == "LDC.L")
                        {
                            if (statement.tokens.Count >= 1
                                && statement.tokens[0].parse_type != ParseType.register_indirect_post_increment)
                            {
                                Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    "for LDC.L, argument \"" + tokens[0].raw_string + "\" is not a register indirect post increment (example: @R0+) like expected.");
                            }

                            ender = 0b0111;
                        } else if (statement.tokens.Count >= 1
                                && statement.tokens[0].parse_type != ParseType.register_direct)
                        {
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                "for LDC, argument \"" + tokens[0].raw_string + "\" is not a register direct (example: R0) like expected.");
                        }

                        if (check_arguments(statement, ParseType.register_direct, ParseType.other_register)
                            || check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.other_register)
                            || check_arguments(statement, ParseType.register_direct, ParseType.gbr_register)
                            || check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.gbr_register))
                        {
                            int register_index = 0;

                            switch (tokens[1].raw_string.ToUpperInvariant())
                            {
                                case "SR":
                                    register_index = 0b0000;
                                    break;
                                case "GBR":
                                    register_index = 0b0001;
                                    break;
                                case "VBR":
                                    register_index = 0b0010;
                                    break;
                                case "SSR":
                                    register_index = 0b0011;
                                    break;
                                case "SPC":
                                    register_index = 0b0100;
                                    break;
                                case "DBR":
                                    register_index = 0b1111;

                                    if (statement.instruction == "LDC.L")
                                    {
                                        ender = 0b0110;
                                    }
                                    else
                                    {
                                        ender = 0b1010;
                                    }
                                    break;
                                default:
                                    Error(statement.raw_line, statement.module, statement.line_number, -1,
                                        "invalid " + statement.instruction + " special register argument " + tokens[1].raw_string);
                                    break;
                            }
                            return generate_register((ushort)(0b0100_0000_0000_0000 | (register_index << 4) | ender), statement, 0);

                        }
                        else if (check_arguments(statement, ParseType.register_direct, ParseType.r_bank_register_direct))
                        {
                            return generate_register_register(0b0100_0000_1000_1110, statement);
                        }
                        else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.r_bank_register_direct))
                        {
                            return generate_register_register(0b0100_0000_1000_0111, statement);
                        }
                    }
                    break;
                case "LDS":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b0100_0000_0101_1010, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_direct, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "FPSCR")
                    {
                        return generate_register(0b0100_0000_0110_1010, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_direct, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "MACH")
                    {
                        return generate_register(0b0100_0000_0000_1010, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_direct, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "MACL")
                    {
                        return generate_register(0b0100_0000_0001_1010, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_direct, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "PR")
                    {
                        return generate_register(0b0100_0000_0010_1010, statement, 0);
                    }
                    break;
                case "LDS.L":
                    if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.other_register)
                        && tokens[1].raw_string.ToUpperInvariant() == "FPUL")
                    {
                        return generate_register(0b0100_0000_0101_0110, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.other_register)
                      && tokens[1].raw_string.ToUpperInvariant() == "FPSCR")
                    {
                        return generate_register(0b0100_0000_0110_0110, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.other_register)
                      && tokens[1].raw_string.ToUpperInvariant() == "MACH")
                    {
                        return generate_register(0b0100_0000_0000_0110, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.other_register)
                      && tokens[1].raw_string.ToUpperInvariant() == "MACL")
                    {
                        return generate_register(0b0100_0000_0001_0110, statement, 0);
                    }
                    else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.other_register)
                      && tokens[1].raw_string.ToUpperInvariant() == "PR")
                    {
                        return generate_register(0b0100_0000_0010_0110, statement, 0);
                    }
                    break;
                case "LDTLB":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000111000;
                    }
                    break;
                case "MAC.L":
                    if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.register_indirect_post_increment))
                    {
                        return generate_register_register_swapped(0b0000_0000_0000_1111, statement);
                    }
                    break;
                case "MAC.W":
                case "MAC":
                    if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.register_indirect_post_increment))
                    {
                        return generate_register_register_swapped(0b0100_0000_0000_1111, statement);
                    }
                    break;
                case "MOV":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_0011, statement);
                    }
                    else if (check_arguments(statement, ParseType.integer_number, ParseType.register_direct))
                    {
                        return generate_immediate_register(0b1110, statement);
                    }
                    break;
                case "MOV.B":
                    return generate_mov(statement, 0);
                case "MOV.W":
                    return generate_mov(statement, 1);
                case "MOV.L":
                    return generate_mov(statement, 2);
                case "MOVA":
                    if (check_arguments(statement, ParseType.pc_displacement, ParseType.register_direct))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_pc_displacement8(0b1100_0111, statement, 4);
                    }
                    break;
                case "MOVCA.L":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_indirect))
                    {
                        check_error_require_register_zero(statement, 0);
                        return generate_register(0b0000_0000_1100_0011, statement, 1);
                    }
                    break;
                case "MOVT":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0000_0000_0010_1001, statement, 0);
                    }
                    break;
                case "MUL.L":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0000_0000_0000_0111, statement);
                    }
                    break;
                case "MULS":
                case "MULS.W":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1111, statement);
                    }
                    break;
                case "MULU":
                case "MULU.W":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1110, statement);
                    }
                    break;
                case "NEG":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1011, statement);
                    }
                    break;
                case "NEGC":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1010, statement);
                    }
                    break;
                case "NOP":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000001001;
                    }
                    break;
                case "NOT":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_0111, statement);
                    }
                    break;
                case "OCBI":
                    if (check_arguments(statement, ParseType.register_indirect))
                    {
                        return generate_register(0b0000_0000_1001_0011, statement, 0);
                    }
                    break;
                case "OCBP":
                    if (check_arguments(statement, ParseType.register_indirect))
                    {
                        return generate_register(0b0000_0000_1010_0011, statement, 0);
                    }
                    break;
                case "OCBWB":
                    if (check_arguments(statement, ParseType.register_indirect))
                    {
                        return generate_register(0b0000_0000_1011_0011, statement, 0);
                    }
                    break;
                case "OR":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1011, statement);
                    }
                    else if (check_arguments(statement, ParseType.integer_number, ParseType.register_direct))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1011, statement);
                    }
                    break;
                case "OR.B":
                    if (check_arguments(statement, ParseType.integer_number, ParseType.gbr_indirect_indexed))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1111, statement);
                    }
                    break;
                case "PREF":
                    if (check_arguments(statement, ParseType.register_indirect))
                    {
                        return generate_register(0b0000_0000_1000_0011, statement, 0);
                    }
                    break;
                case "ROTCL":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0010_0100, statement, 0);
                    }
                    break;
                case "ROTCR":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0010_0101, statement, 0);
                    }
                    break;
                case "ROTL":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0000_0100, statement, 0);
                    }
                    break;
                case "ROTR":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0000_0101, statement, 0);
                    }
                    break;
                case "RTE":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000101011;
                    }
                    break;
                case "RTS":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000001011;
                    }
                    break;
                case "SETS":
                    if (check_arguments(statement))
                    {
                        return 0b0000000001011000;
                    }
                    break;
                case "SETT":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000011000;
                    }
                    break;
                case "SHAD":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0100_0000_0000_1100, statement);
                    }
                    break;
                case "SHAL":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0010_0000, statement, 0);
                    }
                    break;
                case "SHAR":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0010_0001, statement, 0);
                    }
                    break;
                case "SHLD":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0100_0000_0000_1101, statement);
                    }
                    break;
                case "SHLL":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0000_0000, statement, 0);
                    }
                    break;
                case "SHLL2":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0000_1000, statement, 0);
                    }
                    break;
                case "SHLL8":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0001_1000, statement, 0);
                    }
                    break;
                case "SHLL16":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0010_1000, statement, 0);
                    }
                    break;
                case "SHLR":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0000_0001, statement, 0);
                    }
                    break;
                case "SHLR2":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0000_1001, statement, 0);
                    }
                    break;
                case "SHLR8":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0001_1001, statement, 0);
                    }
                    break;
                case "SHLR16":
                    if (check_arguments(statement, ParseType.register_direct))
                    {
                        return generate_register(0b0100_0000_0010_1001, statement, 0);
                    }
                    break;
                case "SLEEP":
                    if (check_arguments(statement))
                    {
                        return 0b0000000000011011;
                    }
                    break;
                case "STC":
                    return generate_stc(statement, false);
                case "STC.L":
                    return generate_stc(statement, true);
                case "STS":
                    return generate_sts(statement, false);
                case "STS.L":
                    return generate_sts(statement, true);
                case "SUB":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_1000, statement);
                    }
                    break;
                case "SUBC":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_1010, statement);
                    }
                    break;
                case "SUBV":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0011_0000_0000_1011, statement);
                    }
                    break;
                case "SWAP.B":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1000, statement);
                    }
                    break;
                case "SWAP.W":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0110_0000_0000_1001, statement);
                    }
                    break;
                case "TAS.B":
                    if (check_arguments(statement, ParseType.register_indirect))
                    {
                        return generate_register(0b0100_0000_0001_1011, statement, 0);
                    }
                    break;
                case "TRAPA":
                    if (check_arguments(statement, ParseType.integer_number))
                    {
                        return generate_immediate8(0b1100_0011, statement);
                    }
                    break;
                case "TST":
                    if (check_arguments(statement, ParseType.integer_number, ParseType.register_direct))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1000, statement);
                    }
                    else if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1000, statement);
                    }
                    break;
                case "TST.B":
                    if (check_arguments(statement, ParseType.integer_number, ParseType.gbr_indirect_indexed))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1100, statement);
                    }
                    break;
                case "XOR":
                    if (check_arguments(statement, ParseType.integer_number, ParseType.register_direct))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1010, statement);
                    }
                    else if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1010, statement);
                    }
                    break;
                case "XOR.B":
                    if (check_arguments(statement, ParseType.integer_number, ParseType.gbr_indirect_indexed))
                    {
                        check_error_require_register_zero(statement, 1);
                        return generate_immediate8(0b1100_1110, statement);
                    }
                    break;
                case "XTRCT":
                    if (check_arguments(statement, ParseType.register_direct, ParseType.register_direct))
                    {
                        return generate_register_register_swapped(0b0010_0000_0000_1101, statement);
                    }
                    break;
                default:
                    Error(statement.raw_line, statement.module, statement.line_number, -1, "unknown instruction: " + statement.instruction);
                    break;
            }

            error_wrong_args(statement);

            throw new Exception("code should never reach here, but compiler complains if we don't have this");
        }

        static ushort generate_sts(Statement statement, bool b_l)
        {
            if (b_l)
            {
                if (!check_arguments(statement, ParseType.other_register, ParseType.register_indirect_pre_decrement))
                {
                    error_wrong_args(statement);
                }
            }
            else if (!check_arguments(statement, ParseType.other_register, ParseType.register_direct))
            {
                error_wrong_args(statement);
            }

            int output = 0b0000_0000_0000_1000;

            if (b_l)
            {
                output = 0b0100_0000_0000_0000;
            }

            switch (statement.tokens[0].raw_string.ToUpperInvariant())
            {
                case "MACH":
                    output |= 0b0000_0010;
                    break;
                case "MACL":
                    output |= 0b0001_0010;
                    break;
                case "PR":
                    output |= 0b0010_0010;
                    break;
                case "FPUL":
                    output |= 0b0101_0010;
                    break;
                case "FPSCR":
                    output |= 0b0110_0010;
                    break;
                default:
                    // fixme more specific error message
                    error_wrong_args(statement);
                    break;
            }
            
            return generate_register((ushort)output, statement, 1);
        }

        static ushort generate_stc(Statement statement, bool b_l)
        {
            if (statement.tokens.Count != 2)
            {
                error_wrong_args(statement);
            }

            if (b_l) //stc.l
            {
                if (check_arguments(statement, ParseType.other_register, ParseType.register_indirect_pre_decrement)
                    || check_arguments(statement, ParseType.gbr_register, ParseType.register_indirect_pre_decrement))
                {
                    int special_value = 0;

                    switch (statement.tokens[0].raw_string.ToUpperInvariant())
                    {
                        case "SR":
                            special_value = 0b0000_0011;
                            break;
                        case "GBR":
                            special_value = 0b0001_0011;
                            break;
                        case "VBR":
                            special_value = 0b0010_0011;
                            break;
                        case "SSR":
                            special_value = 0b0011_0011;
                            break;
                        case "SPC":
                            special_value = 0b0100_0011;
                            break;
                        case "SGR":
                            special_value = 0b0011_0010;
                            break;
                        case "DBR":
                            special_value = 0b1111_0010;
                            break;
                        default:
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                "invalid " + statement.instruction + " special register argument " + statement.tokens[0].raw_string);
                            break;
                    }

                    int register_index = (int)statement.tokens[1].value;

                    return (ushort)(special_value | (register_index << 8) | (0b0100 << 12));
                }
                else if (check_arguments(statement, ParseType.r_bank_register_direct, ParseType.register_indirect_pre_decrement))
                {
                    return generate_register_register_swapped(0b0100_0000_1000_0011, statement);
                }
            }
            else
            {
                if (check_arguments(statement, ParseType.other_register, ParseType.register_direct)
                    || check_arguments(statement, ParseType.gbr_register, ParseType.register_direct))
                {
                    int special_value = 0;

                    switch (statement.tokens[0].raw_string.ToUpperInvariant())
                    {
                        case "SR":
                            special_value = 0b0000_0010;
                            break;
                        case "GBR":
                            special_value = 0b0001_0010;
                            break;
                        case "VBR":
                            special_value = 0b0010_0010;
                            break;
                        case "SSR":
                            special_value = 0b0011_0010;
                            break;
                        case "SPC":
                            special_value = 0b0100_0010;
                            break;
                        case "SGR":
                            special_value = 0b0011_1010;
                            break;
                        case "DBR":
                            special_value = 0b1111_1010;
                            break;
                        default:
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                "invalid " + statement.instruction + " special register argument " + statement.tokens[0].raw_string);
                            break;
                    }

                    int register_index = (int)statement.tokens[1].value;

                    return (ushort)(special_value | (register_index << 8));
                }
                else if(check_arguments(statement, ParseType.r_bank_register_direct, ParseType.register_direct))
                {
                    return generate_register_register_swapped(0b0000_0000_1000_0010, statement);
                }
            }

            error_wrong_args(statement);
            throw new Exception("code should never reach here, but compiler complains if we don't have this");
        }

        static ushort generate_mov(Statement statement, ushort size)
        {
            if (check_arguments(statement, ParseType.register_direct, ParseType.register_indirect_displacement))
            {
                switch (size)
                {
                    case 0:
                        check_error_require_register_zero(statement, 0);
                        return generate_displacement4_register(0b10000000, statement, 1, 1, (int)statement.tokens[1].inner_token2.value);
                    case 1:
                        check_error_require_register_zero(statement, 0);
                        return generate_displacement4_register(0b10000001, statement, 2, 1, (int)statement.tokens[1].inner_token2.value);
                    case 2:
                        return generate_displacement4_register_register(0b0001, statement, 4, true);
                    default:
                        throw new Exception("weird size " + size + " in generate_mov, serious error");
                }
            }
            else if (check_arguments(statement, ParseType.register_indirect_displacement, ParseType.register_direct))
            {
                switch (size)
                {
                    case 0:
                        check_error_require_register_zero(statement, 1);
                        return generate_displacement4_register(0b10000100, statement, 1, 0, (int)statement.tokens[0].inner_token2.value);
                    case 1:
                        check_error_require_register_zero(statement, 1);
                        return generate_displacement4_register(0b10000101, statement, 2, 0, (int)statement.tokens[0].inner_token2.value);
                    case 2:
                        return generate_displacement4_register_register(0b0101, statement, 4);
                    default:
                        throw new Exception("weird size " + size + " in generate_mov, serious error");
                }
            }

            int displacement_size = 1;

            if (size == 1)
            {
                displacement_size = 2;

                if (check_arguments(statement, ParseType.pc_displacement, ParseType.register_direct))
                {
                    return generate_displacement8_register(0b1001, statement, displacement_size);
                }
            }
            else if (size == 2)
            {
                displacement_size = 4;

                if (check_arguments(statement, ParseType.pc_displacement, ParseType.register_direct))
                {
                    return generate_displacement8_register(0b1101, statement, displacement_size);
                }
            }

            if (check_arguments(statement, ParseType.gbr_indirect_displacement, ParseType.register_direct))
            {
                return generate_gbr_displacement((ushort)(0b11000100 | size), statement, displacement_size, 0);
            }
            else if (check_arguments(statement, ParseType.register_direct, ParseType.gbr_indirect_displacement))
            {
                return generate_gbr_displacement((ushort)(0b11000000 | size), statement, displacement_size, 1);
            }

            int code = size;
            if (check_arguments(statement, ParseType.register_direct, ParseType.register_indirect))
            {
                code |= 0b0010_0000_0000_0000;
            }
            else if (check_arguments(statement, ParseType.register_indirect, ParseType.register_direct))
            {
                code |= 0b0110_0000_0000_0000;
            }
            else if (check_arguments(statement, ParseType.register_direct, ParseType.register_indirect_pre_decrement))
            {
                code |= 0b0010_0000_0000_0100;
            }
            else if (check_arguments(statement, ParseType.register_indirect_post_increment, ParseType.register_direct))
            {
                code |= 0b0110_0000_0000_0100;
            }
            else if (check_arguments(statement, ParseType.register_direct, ParseType.register_indexed_indirect))
            {
                code |= 0b0000_0000_0000_0100;
            }
            else if (check_arguments(statement, ParseType.register_indexed_indirect, ParseType.register_direct))
            {
                code |= 0b0000_0000_0000_1100;
            }
            else
            {
                error_wrong_args(statement);
            }

            return generate_register_register_swapped((ushort)code, statement);

            throw new Exception("code should never reach here, but compiler complains if we don't have this");
        }

        static void check_error_require_register_zero(Statement statement, int index, string register_name = "R0")
        {
            if (statement.tokens[index].value != 0)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "register argument must be " + register_name + " for this form of " + statement.instruction
                    + ", was \"" + statement.tokens[index].raw_string + "\" instead.");
            }
        }

        static void error_wrong_args(Statement statement)
        {
            Error(statement.raw_line, statement.module, statement.line_number, -1, "wrong arguments for instruction: " + statement.instruction);
        }

        static bool check_arguments(Statement statement, params ParseType[] expected_args)
        {
            int count = expected_args.Length;
            if (statement.tokens.Count != count)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                ParseType expected = expected_args[i];
                ParseType actual = statement.tokens[i].parse_type;

                if (expected != actual)
                {
                    if (expected == ParseType.integer_number
                        && actual == ParseType.hex_number)
                    {
                        continue;
                    }
                    else if (expected == ParseType.integer_number
                             && actual == ParseType.name)
                    {
                        string key = statement.tokens[i].raw_string.ToUpperInvariant();

                        if (!key.Contains('.'))
                        {
                            key = statement.module + "." + key;
                        }

                        if (symbol_table.ContainsKey(key))
                        {
                            if (symbol_table[key].symbol_type == SymbolType.label
                                || symbol_table[key].symbol_type == SymbolType.from_symbol_directive)
                            {
                                continue;
                            }
                            else
                            {
                                return false;
                            }
                        }
                        else
                        {
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    "can't figure out what \"" + statement.tokens[i].raw_string + "\" is, did you forget to define it, or make a typo?");
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
            } // for 
            return true;
        } // check arguments

        static ushort generate_register(ushort insn, Statement statement, int index)
        {
            ushort register = (ushort)statement.tokens[index].value;
            return (ushort)(insn | (register << 8));
        }

        static ushort generate_fv_register(ushort insn, Statement statement, int index)
        {
            ushort register = (ushort)statement.tokens[index].value;
            return (ushort)(insn | (register << 10));
        }

        static ushort generate_immediate_register(ushort insn, Statement statement)
        {
            ushort immediate = (ushort)(statement.tokens[0].value & 0xFF);
            ushort register = (ushort)statement.tokens[1].value;
            return (ushort)((insn << 12) | (register << 8) | immediate);
        }

        static ushort generate_immediate8(ushort insn, Statement statement)
        {
            ushort immediate = (ushort)(statement.tokens[0].value & 0xFF);
            if (immediate > 255)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "argument \"" + immediate + "\"  for " + statement.instruction + " too big, must be less than 256");
            }

            return (ushort)((insn << 8) | immediate);
        }

        static int calculate_general_displacement(Statement statement, int size, int max, int displacement_index)
        {
            int displacement = (int)statement.tokens[displacement_index].value;

            if (displacement % size != 0)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "displacement argument \"" + displacement + "\"  for " + statement.instruction + " must be " + size + "-aligned (add or remove a single byte #data padding somewhere probably?)");
            }

            displacement = (displacement / size);

            max *= size;

            if (displacement > max)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "displacement argument \"" + (short)statement.tokens[displacement_index].value + "\"  for " + statement.instruction + " too far, can only be closer than +" + max + " bytes away");
            }

            return displacement;
        }

        static ushort generate_displacement4_register(ushort insn, Statement statement, int size, int displacement_index, int register)
        {
            int displacement = calculate_general_displacement(statement, size, 15, displacement_index);

            return (ushort)((insn << 8) | (register << 4) | (ushort)displacement);
        }

        static ushort generate_displacement4_register_register(ushort insn, Statement statement, int size, bool bSwap = false)
        {
            int displacement_index = 0;
            int reg_index = 1;

            if (bSwap)
            {
                reg_index = 0;
                displacement_index = 1;
            }

            int register1 = (int)statement.tokens[reg_index].value;

            int displacement = calculate_general_displacement(statement, size, 15, displacement_index);

            if (statement.tokens[displacement_index].inner_token2 == null)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "displacement argument " + statement.tokens[displacement_index].raw_string + " is missing inner register token");
            }

            int register2 = (int)statement.tokens[displacement_index].inner_token2.value;

            if (bSwap)
            {
                int temp = register1;
                register1 = register2;
                register2 = temp;
            }

            return (ushort)((insn << 12) | (register1 << 8) | (register2 << 4) | (ushort)displacement);
        }

        static ushort generate_gbr_displacement(ushort insn, Statement statement, int size, int token_offset)
        {
            int displacement = (int)statement.tokens[token_offset].value;

            if (statement.tokens[token_offset].parse_type == ParseType.name)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "GBR has to be displaced by number, not displacement argument \"" + statement.tokens[token_offset].raw_string + "\"");
            }

            if (displacement % size != 0)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "displacement argument \"" + displacement + "\"  for " + statement.instruction + " must be " + size + "-aligned (add or remove #data padding somewhere probably?)");
            }

            displacement = displacement / size;

            int max = 255 * size;

            if (displacement > max)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "displacement argument \"" + (short)statement.tokens[token_offset].value + "\"  for " + statement.instruction + " too far, can only be closer than " + max + " bytes away");
            }

            return (ushort)((insn << 8) | (ushort)displacement);
        }

        static int calculate_pc_displacement(Statement statement, int size, int min_base, int max_base)
        {
            long displacement;

            int alignment;

            

            if (statement.tokens[0].parse_type == ParseType.name
                || (statement.tokens[0].inner_token != null && statement.tokens[0].inner_token.parse_type == ParseType.name))
            {
                long base_addr = statement.address;
                long target_addr = unchecked((uint)statement.tokens[0].value);

                if (size == 4)
                {
                    // 4 size has weird thing to force alignment
                    displacement = ((target_addr - base_addr - 2) & 0xfffffffc) / size;
                    alignment = 0;
                }
                else
                {
                    alignment = (int)((target_addr - base_addr - 4) % size);
                    displacement = (target_addr - base_addr - 4) / size;
                }
            }
            else if (statement.tokens[0].parse_type == ParseType.absolute_displacement_address)
            {
                long base_addr = statement.address;
                long target_addr = unchecked((uint)statement.tokens[0].value);

                if (size == 4)
                {
                    // 4 size has weird thing to force alignment
                    displacement = ((target_addr - base_addr - 2) & 0xfffffffc) / size;
                    alignment = 0;
                }
                else
                {
                    alignment = (int)(target_addr - base_addr - 4) % size;
                    displacement = (target_addr - base_addr - 4) / size;
                }
            }
            else
            {
                long raw_displacement = (long)statement.tokens[0].value;
                alignment = (int)raw_displacement % size;

                if (size == 4)
                {
                    displacement = (long)(raw_displacement & 0xfffffffc) / size;
                }
                else
                {
                    displacement = raw_displacement / size;
                }
            }

            if (alignment != 0)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "displacement argument \"" + displacement + "\"  for " + statement.instruction + " must be " + size + "-aligned (add or remove #data padding somewhere probably?)");
            }

            int max = max_base * size;
            int min = min_base * size;

            if (displacement > max || displacement < min)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "displacement argument \"" + (short)statement.tokens[0].value + "\"  for " + statement.instruction + " too far, can only be between " + min + " or +" + max + " bytes away");
            }

            return (int)displacement;
        }

        static ushort generate_pc_displacement8(ushort insn, Statement statement, int size = 2)
        {
            short displacement = (short)calculate_pc_displacement(statement, size, -256, 254);

            return (ushort)((insn << 8) | ((ushort)displacement & 0xFF));
        }

        static ushort generate_displacement8_register(ushort insn, Statement statement, int size = 2)
        {
            ushort register = (ushort)statement.tokens[1].value;
            return generate_pc_displacement8((ushort)((insn << 4) | register), statement, size);
        }

        static ushort generate_pc_displacement12(ushort insn, Statement statement, int size = 2)
        {
            int displacement = calculate_pc_displacement(statement, size, -4096, 4094);

            return (ushort)((insn << 12) | ((ushort)displacement & 0x0FFF));
        }

        static ushort generate_fv_register_register(ushort insn, Statement statement)
        {
            // fv registers already divided by 4
            ushort register1 = (ushort)statement.tokens[0].value;
            ushort register2 = (ushort)statement.tokens[1].value;
            return (ushort)(insn | (register1 << 8) | (register2 << 10));
        }

        static ushort generate_register_register_swapped(ushort insn, Statement statement, int offset = 0)
        {
            ushort register1 = (ushort)statement.tokens[offset + 0].value;
            ushort register2 = (ushort)statement.tokens[offset + 1].value;
            // registers are swapped from order you would expect, weird
            return (ushort)(insn | (register1 << 4) | (register2 << 8));
        }

        static ushort generate_register_register(ushort insn, Statement statement, int offset = 0)
        {
            ushort register1 = (ushort)statement.tokens[offset + 0].value;
            ushort register2 = (ushort)statement.tokens[offset + 1].value;
            return (ushort)(insn | (register1 << 8) | (register2 << 4));
        }

        static void intermediate_step(List<Statement> statements)
        {
            uint current_address = starting_offset;

            // for #repeat
            int prev_repeat_set = 1;

            foreach (Statement statement in statements)
            {
                if (statement.repeat_count <= 0)
                {
                    statement.repeat_count = 1;
                }

                statement.repeat_count *= prev_repeat_set;
                prev_repeat_set = 1;

                if (statement.instruction == "#DATA"
                    || statement.instruction == "#DATA16"
                    || statement.instruction == "#DATA8")
                {
                    for (int i = 0; i < statement.repeat_count; i++)
                    {
                        process_data(statement, ref current_address);
                    }
                }
                else if (statement.instruction == "#ALIGN4" || statement.instruction == "#ALIGN4_NOP")
                {
                    // Console.WriteLine("old: " + current_address);

                    if (statement.repeat_count > 1)
                    {
                        statement.repeat_count = 1;
                        Warn(statement.raw_line, statement.module, statement.line_number, -1,
                                    "repeat has been applied to alignment directive");
                    }

                    statement.address = current_address;
                    while (current_address % 4 != 0)
                    {
                        current_address++;
                    }
                    // Console.WriteLine("new: " + current_address);
                }
                else if (statement.instruction == "#ALIGN16" || statement.instruction == "#ALIGN16_NOP")
                {
                    if (statement.repeat_count > 1)
                    {
                        statement.repeat_count = 1;
                        Warn(statement.raw_line, statement.module, statement.line_number, -1,
                                    "repeat has been applied to alignment directive");
                    }

                    statement.address = current_address;
                    while (current_address % 16 != 0)
                    {
                        current_address++;
                    }
                }
                else if (statement.instruction == "#ALIGN")
                {
                    if (statement.repeat_count > 1)
                    {
                        statement.repeat_count = 1;
                        Warn(statement.raw_line, statement.module, statement.line_number, -1,
                                    "repeat has been applied to alignment directive");
                    }

                    // Console.WriteLine("old: " + current_address);
                    statement.address = current_address;
                    while (current_address % 2 != 0)
                    {
                        current_address++;
                    }
                    // Console.WriteLine("new: " + current_address);
                }
                else if (statement.instruction == "#REPEAT")
                {
                    if (statement.tokens.Count == 0)
                    {
                        prev_repeat_set = prev_repeat_set * 2;
                    }
                    else if (statement.tokens.Count == 1)
                    {
                        assign_value_to_token(statement, statement.tokens[0]);
                        prev_repeat_set = prev_repeat_set * (int)statement.tokens[0].value;
                    }
                    else
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    "too many parameters to #repeat directive");
                    }
                }
                else if (statement.instruction == "#IMPORT_RAW_DATA")
                {
                    process_import_raw_data(statement, ref current_address);
                }
                else if (statement.instruction == "#LITTLE_ENDIAN" || statement.instruction == "#BIG_ENDIAN")
                {
                    // nope
                }
                else if (statement.instruction != "#SYMBOL")
                {
                    for (int i = 0; i < statement.repeat_count; i++)
                    {
                        process_statement(statement, ref current_address);
                    }
                }
            }

            foreach (KeyValuePair<string, Symbol> pair in symbol_table)
            {
                Symbol label = pair.Value;
                if (label.symbol_type == SymbolType.label)
                {
                    int current_statement_index = label.statement_number;

                    if (current_statement_index == statements.Count)
                    {
                        // the end of the file
                        label.address = current_address;
                        label.value = label.address;
                    }
                    else
                    {
                        uint new_address = statements[current_statement_index].address;

                        // todo figure out if we need to do this some other way
                        while (current_statement_index < statements.Count - 1
                            && (
                                    statements[current_statement_index].instruction == "#ALIGN4"
                                    || statements[current_statement_index].instruction == "#ALIGN4_NOP"
                                    || statements[current_statement_index].instruction == "#ALIGN16"
                                    || statements[current_statement_index].instruction == "#ALIGN16_NOP"
                                    || statements[current_statement_index].instruction == "#ALIGN"
                                )
                            )
                        {
                            current_statement_index++;
                            new_address = statements[current_statement_index].address;
                        }

                        if (current_statement_index != label.statement_number
                            && (
                                    statements[current_statement_index].instruction == "#ALIGN4"
                                    || statements[current_statement_index].instruction == "#ALIGN4_NOP"
                                    || statements[current_statement_index].instruction == "#ALIGN16"
                                    || statements[current_statement_index].instruction == "#ALIGN16_NOP"
                                    || statements[current_statement_index].instruction == "#ALIGN"
                                )
                            )
                        {
                            label.statement_number = current_statement_index;

                            label.address = new_address;
                        }
                        else
                        {
                            label.address = statements[label.statement_number].address;
                        }

                        label.value = label.address;
                        //Console.WriteLine("label " + label.name + " with address " + label.address.ToString("X2"));
                    }
                }
            }

            bool bErrored = false;
            foreach (Statement statement in statements)
            {
                foreach (Token token in statement.tokens)
                {
                    try
                    {
                        assign_value_to_token(statement, token);
                    }
                    catch (Exception e)
                    {
                        bErrored = true;
                    }
                }
            }

            if (bErrored)
            {
                throw new Exception("NO ERROR HANDLING YET SORRY");
            }
        }

        static void assign_value_to_token(Statement statement, Token token)
        {
            if (token.is_value_assigned)
            {
                return;
            }

            uint default_size = 4;
            if (statement.instruction == "#DATA8")
            {
                default_size = 1;
            }
            else if (statement.instruction == "#DATA16")
            {
                default_size = 2;
            }

            switch (token.parse_type)
            {
                case ParseType.expression:
                    assign_value_to_expression(statement, token);
                    token.size = default_size;
                    break;
                case ParseType.name:
                    {
                        Symbol s = resolve_name(statement, token.raw_string);

                        token.value = s.value;
                        token.size = (uint)Math.Min(s.size, default_size);
                        token.is_value_assigned = true;
                    }
                    break;
                case ParseType.integer_number:
                    token.is_value_assigned = true;
                    token.value = long.Parse(token.raw_string, CultureInfo.InvariantCulture);
                    token.size = default_size;
                    break;
                case ParseType.absolute_displacement_address:
                case ParseType.hex_number:
                    token.is_value_assigned = true;
                    token.value = Convert.ToInt64(token.raw_string, 16);
                    // FIXME need error on odd number
                    token.size = (uint)(token.raw_string.Length - 2) / 2;
                    if (default_size != 4 && token.size > default_size)
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    "hex number " + token.raw_string + " is larger than " + default_size + " bytes");
                    }
                    break;
                case ParseType.float_number:
                    token.is_value_assigned = true;
                    token.value = (long)BitConverter.ToUInt32(BitConverter.GetBytes(float.Parse(token.raw_string, CultureInfo.InvariantCulture)),0);
                    token.size = default_size;

                    if (default_size < 4)
                    {
                        token.value >>= (4 - (int)default_size) * 8;
                    }
                    break;
                case ParseType.register_indexed_indirect:
                    if (token.inner_token == null || token.inner_token2 == null)
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    "error finding inner token of indirect addressing token \"" + token.raw_string + "\", this is probably an assembler error on our part? please let us know.");
                    }
                    else if (token.inner_token2 != null)
                    {
                        assign_value_to_token(statement, token.inner_token);

                        if (token.inner_token.value != 0)
                        {
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    "first register in indirect addressing must be R0, was  \"" + token.inner_token.raw_string + "\" instead");
                        }

                        assign_value_to_token(statement, token.inner_token2);

                        token.value = token.inner_token2.value;
                        token.is_value_assigned = true;
                    }
                    break;
                case ParseType.string_data:
                    token.is_value_assigned = true;
                    token.value = 0;
                    token.size = (uint)token.raw_string.Length;

                    if (default_size != 4 && default_size != token.size)
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    "string \"" + token.raw_string + "\" is not exactly " + default_size + " bytes long");
                    }
                    break;
                default:
                    if (token.inner_token != null)
                    {
                        assign_value_to_token(statement, token.inner_token);

                        token.is_value_assigned = true;
                        token.value = token.inner_token.value;
                    }
                    break;
            }
        }

        private static Symbol resolve_name(Statement statement, string raw_key)
        {
            string key = raw_key.ToUpperInvariant();
            bool bFound = symbol_table.ContainsKey(key);

            if (!bFound)
            {
                if (!key.Contains("."))
                {
                    key = statement.module + "." + key;
                }

                bFound = symbol_table.ContainsKey(key);
            }

            if (bFound)
            {
                if (symbol_table[key].symbol_type == SymbolType.instruction)
                {
                    Error(statement.raw_line, statement.module, statement.line_number, -1,
                            raw_key + " is an instruction, but is being used as an argument instead. did you forget a newline?");
                }
                else
                {
                    Symbol symbol = symbol_table[key];

                    if (symbol.has_been_associated)
                    {
                        return symbol;
                    }
                    else
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "value of '" + raw_key + "' can't be resolved. probably can't use a label here");
                    }
                    //Console.WriteLine(key + " with value " + token.value.ToString("X2"));
                }
            }
            else
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "i don't know what " + raw_key + " is, did you forget to define it, or make a typo?");
            }

            return null;
        }

        static void assign_value_to_expression(Statement statement, Token token)
        {
            // before we calculate we should make sure the expression has balanced parenthesis
            int subtokens_count = token.expression.subtokens_type.Count;
            int parens_counter = 0;
            for (int index = 0; index < subtokens_count; index++)
            {
                if (token.expression.subtokens_type[index] == Expression.SubtokenType.open_parenthesis)
                {
                    parens_counter++;
                }
                else if (token.expression.subtokens_type[index] == Expression.SubtokenType.close_parenthesis)
                {
                    if (parens_counter <= 0)
                    {
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "unexpected closing ')' without corresponding '(' in expression {" + token.raw_string + "}");
                    }
                    parens_counter--;
                }
            }

            if (parens_counter > 0)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "unclosed '(', expected a corresponding ')' somewhere in expression {" + token.raw_string + "}");
            }
            
            token.value = calculate_expression(statement, token.expression, 0, token.expression.subtokens.Count);

            //Console.WriteLine(token.raw_string + " had value = " + token.value);
        }

        static long calculate_expression(Statement statement, Expression e, int start_index, int end_index)
        {
            int subtoken_count = end_index - start_index;

            if (subtoken_count == 1)
            {
                if (e.subtokens_type[start_index] == Expression.SubtokenType.name)
                {
                    Symbol s = resolve_name(statement, e.subtokens[start_index]);
                    return s.value;
                }
                else if (e.subtokens_type[start_index] == Expression.SubtokenType.decimal_number)
                {
                    return long.Parse(e.subtokens[start_index], CultureInfo.InvariantCulture);
                }
                else if (e.subtokens_type[start_index] == Expression.SubtokenType.hex_number)
                {
                    return Convert.ToInt64(e.subtokens[start_index], 16);
                }
                else
                {
                    Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "expression processing error near " + e.subtokens[start_index] +
                            ", expected a symbol or a number here.");
                }
            }
            else if (subtoken_count == 2)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "expression processing error near '" + e.subtokens[start_index] +
                            "' and '" + e.subtokens[end_index-1] +
                            "', perhaps a left or right hand side of an operation is missing?");
            }
            else if (e.subtokens_type[start_index] == Expression.SubtokenType.open_parenthesis
                && e.subtokens_type[end_index - 1] == Expression.SubtokenType.close_parenthesis)
            {
                if (subtoken_count == 3)
                {
                    return calculate_expression(statement, e, start_index + 1, end_index - 1);
                }

                int parens_counter = 0;

                for (int index = start_index; index < end_index - 1; index++)
                {
                    if (e.subtokens_type[index] == Expression.SubtokenType.open_parenthesis)
                    {
                        parens_counter++;
                    }
                    else if (e.subtokens_type[index] == Expression.SubtokenType.close_parenthesis)
                    {
                        parens_counter--;
                    }

                    if (parens_counter <= 0)
                    {
                        break;
                    }
                }

                if (parens_counter == 1)
                {
                    return calculate_expression(statement, e, start_index + 1, end_index - 1);
                }
            }

            int lowest_index = start_index;
            int lowest_score = score_subtoken_order_of_operations(e.subtokens_type[start_index]);
            Expression.SubtokenType lowest_operator = e.subtokens_type[start_index];

            {
                int parens_counter = 0;

                if (e.subtokens_type[start_index] == Expression.SubtokenType.open_parenthesis)
                {
                    parens_counter++;
                }

                // scan for lowest priority operator in order of ops
                for (int index = start_index + 1; index < end_index; index++)
                {
                    if (e.subtokens_type[index] == Expression.SubtokenType.open_parenthesis)
                    {
                        parens_counter++;
                    }
                    else if (e.subtokens_type[index] == Expression.SubtokenType.close_parenthesis)
                    {
                        parens_counter--;
                    }
                    else if (parens_counter <= 0)
                    {
                        int score = score_subtoken_order_of_operations(e.subtokens_type[index]);

                        if (score <= lowest_score)
                        {
                            lowest_index = index;
                            lowest_score = score;
                            lowest_operator = e.subtokens_type[index];
                        }
                    }
                }
            }

            if (lowest_index == start_index)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "expression processing error near " + e.subtokens[start_index] +
                            ", perhaps a left or right hand side of an operation is missing?");
            }

            long lhs = calculate_expression(statement, e, start_index, lowest_index);
            long rhs = calculate_expression(statement, e, lowest_index + 1, end_index);

            if (lowest_operator == Expression.SubtokenType.add)
            {
                return lhs + rhs;
            }
            else if (lowest_operator == Expression.SubtokenType.subtract)
            {
                return lhs - rhs;
            }
            else if (lowest_operator == Expression.SubtokenType.multiply)
            {
                return lhs * rhs;
            }
            else if (lowest_operator == Expression.SubtokenType.divide)
            {
                return lhs / rhs;
            }

            Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "expression processing error near " + e.subtokens[lowest_index] +
                            ", i expected that to be an operator, but it doesnt seem to be?");

            return 0;
        }

        static int score_subtoken_order_of_operations(Expression.SubtokenType t)
        {
            switch (t)
            {
                case Expression.SubtokenType.add:
                case Expression.SubtokenType.subtract:
                    return 0;
                case Expression.SubtokenType.multiply:
                case Expression.SubtokenType.divide:
                    return 5;
                default:
                    return 100;
            }
        }

        static void process_import_raw_data(Statement statement, ref uint current_address)
        {
            statement.address = current_address;

            if (statement.tokens != null && statement.tokens.Count == 1)
            {
                Token t = statement.tokens[0];
                if (t.parse_type == ParseType.string_data)
                {
                    string filename = Path.Combine(working_directory, t.raw_string);

                    if (File.Exists(filename))
                    {
                        long size = new FileInfo(filename).Length;

                        if (size > int.MaxValue)
                        {
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                filename + " is too large, must be less than or equal to " + int.MaxValue + " bytes");
                        }
                        else
                        {
                            t.size = (uint)size;
                            t.value = t.size;
                            t.is_value_assigned = true;
                            current_address += ((uint)size) * (uint)statement.repeat_count;
                            return;
                        }
                    }
                }
                else
                {
                    Error(statement.raw_line, statement.module, statement.line_number, -1,
                        t.raw_string + " is a symbol that exists, but not of the right type to use for #data");
                }
            }
            else
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                    "wrong number of inputs for " + statement.instruction + " directive, need a single filename");
            }
        }

        static void process_data(Statement statement, ref uint current_address)
        {
            statement.address = current_address;

            uint max_size = 4;

            if (statement.instruction == "#DATA16")
            {
                max_size = 2;
            }
            else if (statement.instruction == "#DATA8")
            {
                max_size = 1;
            }



            foreach (Token t in statement.tokens)
            {
                //long value = 0;
                switch (t.parse_type)
                {
                    case ParseType.expression:
                        current_address += max_size;
                        break;
                    case ParseType.integer_number:
                        current_address += max_size;
                        break;
                    case ParseType.hex_number:
                        current_address += (uint)(t.raw_string.Length - 2) / 2;
                        break;
                    case ParseType.float_number:
                        current_address += max_size;
                        break;
                    case ParseType.string_data:
                        current_address += (uint)t.raw_string.Length;
                        break;
                    case ParseType.name:
                        string key = t.raw_string.ToUpperInvariant();
                        
                        if (!key.Contains("."))
                        {
                            key = statement.module + "." + key;
                        }

                        if (symbol_table.ContainsKey(key))
                        {
                            Symbol label = symbol_table[key];
                            if (label.symbol_type == SymbolType.from_symbol_directive
                                || label.symbol_type == SymbolType.label)
                            {
                                current_address += max_size;
                            }
                            else
                            {
                                Error(statement.raw_line, statement.module, statement.line_number, -1,
                                    t.raw_string + " is a symbol that exists, but not of the right type to use for #data");
                            }
                        }
                        else
                        {
                            Error(statement.raw_line, statement.module, statement.line_number, -1,
                                t.raw_string + " is not a valid #data integer number, label, or symbol");
                        }
                        break;
                    default:
                        Error(statement.raw_line, statement.module, statement.line_number, -1,
                            t.raw_string + " is not a valid #data integer number");
                        break;
                }
            }

            if (max_size < 4 && statement.address + max_size != current_address)
            {
                Error(statement.raw_line, statement.module, statement.line_number, -1,
                            "size mismatch in " + statement.instruction + " expected " + max_size
                            + " bytes, found " + (statement.address - current_address) + " bytes.");
            }
        }

        static void process_statement(Statement statement, ref uint current_address)
        {
            statement.address = current_address;
            current_address += 2;
        }

        static void init_symbols()
        {
            symbol_table = new Dictionary<string, Symbol>();

            add_instruction_symbol("ADD");
            add_instruction_symbol("ADDC");
            add_instruction_symbol("ADDV");
            add_instruction_symbol("AND");
            add_instruction_symbol("BF");
            add_instruction_symbol("BF/S");
            add_instruction_symbol("BF.S");
            add_instruction_symbol("BRA");
            add_instruction_symbol("BRAF");
            add_instruction_symbol("BSR");
            add_instruction_symbol("BSRF");
            add_instruction_symbol("BT");
            add_instruction_symbol("BT/S");
            add_instruction_symbol("BT.S");
            add_instruction_symbol("CLRMAC");
            add_instruction_symbol("CLRS");
            add_instruction_symbol("CLRT");
            add_instruction_symbol("CMP/EQ");
            add_instruction_symbol("CMP/GE");
            add_instruction_symbol("CMP/GT");
            add_instruction_symbol("CMP/HI");
            add_instruction_symbol("CMP/HS");
            add_instruction_symbol("CMP/PL");
            add_instruction_symbol("CMP/PZ");
            add_instruction_symbol("CMP/STR");
            add_instruction_symbol("DIV0S");
            add_instruction_symbol("DIV0U");
            add_instruction_symbol("DIV1");
            add_instruction_symbol("DMULS.L");
            add_instruction_symbol("DMULU.L");
            add_instruction_symbol("DT");
            add_instruction_symbol("EXTS");
            add_instruction_symbol("EXTS.B");
            add_instruction_symbol("EXTS.W");
            add_instruction_symbol("EXTU");
            add_instruction_symbol("EXTU.B");
            add_instruction_symbol("EXTU.W");
            add_instruction_symbol("FABS");
            add_instruction_symbol("FADD");
            add_instruction_symbol("FCMP");
            add_instruction_symbol("FCNVDS");
            add_instruction_symbol("FCNVSD");
            add_instruction_symbol("FDIV");
            add_instruction_symbol("FIPR");
            add_instruction_symbol("FLDI0");
            add_instruction_symbol("FLDI1");
            add_instruction_symbol("FLDS");
            add_instruction_symbol("FLOAT");
            add_instruction_symbol("FMAC");
            add_instruction_symbol("FMOV");
            add_instruction_symbol("FMOV.S");
            add_instruction_symbol("FMOV/S");
            add_instruction_symbol("FMUL");
            add_instruction_symbol("FNEG");
            add_instruction_symbol("FRCHG");
            add_instruction_symbol("FSCHG");
            add_instruction_symbol("FSQRT");
            add_instruction_symbol("FSTS");
            add_instruction_symbol("FSUB");
            add_instruction_symbol("FTRC");
            add_instruction_symbol("FTRV");
            add_instruction_symbol("JMP");
            add_instruction_symbol("JSR");
            add_instruction_symbol("LDC");
            add_instruction_symbol("LDC.L");
            add_instruction_symbol("LDC/L");
            add_instruction_symbol("LDS");
            add_instruction_symbol("LDS.L");
            add_instruction_symbol("LDS/L");
            add_instruction_symbol("LDTLB");
            add_instruction_symbol("MAC.L");
            add_instruction_symbol("MAC.W");
            add_instruction_symbol("MOV");
            add_instruction_symbol("MOV.B");
            add_instruction_symbol("MOV.W");
            add_instruction_symbol("MOV.L");
            add_instruction_symbol("MOVA");
            add_instruction_symbol("MOVCA.L");
            add_instruction_symbol("MOVT");
            add_instruction_symbol("MUL.L");
            add_instruction_symbol("MULS.W");
            add_instruction_symbol("MULU.W");
            add_instruction_symbol("NEG");
            add_instruction_symbol("NEGC");
            add_instruction_symbol("NOP");
            add_instruction_symbol("NOT");
            add_instruction_symbol("OCBI");
            add_instruction_symbol("OCBP");
            add_instruction_symbol("OCBWB");
            add_instruction_symbol("OR");
            add_instruction_symbol("PREF");
            add_instruction_symbol("ROTCL");
            add_instruction_symbol("ROTCR");
            add_instruction_symbol("ROTL");
            add_instruction_symbol("ROTR");
            add_instruction_symbol("RTE");
            add_instruction_symbol("RTS");
            add_instruction_symbol("SETS");
            add_instruction_symbol("SETT");
            add_instruction_symbol("SHAD");
            add_instruction_symbol("SHAL");
            add_instruction_symbol("SHAR");
            add_instruction_symbol("SHLD");
            add_instruction_symbol("SHLL");
            add_instruction_symbol("SHLL2");
            add_instruction_symbol("SHLL8");
            add_instruction_symbol("SHLL16");
            add_instruction_symbol("SHLR");
            add_instruction_symbol("SHLR2");
            add_instruction_symbol("SHLR8");
            add_instruction_symbol("SHLR16");
            add_instruction_symbol("SLEEP");
            add_instruction_symbol("STC");
            add_instruction_symbol("STC.L");
            add_instruction_symbol("STS");
            add_instruction_symbol("STS.L");
            add_instruction_symbol("SUB");
            add_instruction_symbol("SUBC");
            add_instruction_symbol("SUBV");
            add_instruction_symbol("SWAP");
            add_instruction_symbol("TAS");
            add_instruction_symbol("TRAPA");
            add_instruction_symbol("TST");
            add_instruction_symbol("XOR");
            add_instruction_symbol("XTRCT");

            add_builtin_symbol("#DATA");
            add_builtin_symbol("#DATA8");
            add_builtin_symbol("#DATA16");
            add_builtin_symbol_alias("#D", "#DATA");
            add_builtin_symbol_alias("#D8", "#DATA8");
            add_builtin_symbol_alias("#D16", "#DATA16");
            add_builtin_symbol("#SYMBOL");
            add_builtin_symbol("#REPEAT");
            add_builtin_symbol("#ALIGN4");
            add_builtin_symbol("#ALIGN");
            add_builtin_symbol("#ALIGN4_NOP");
            add_builtin_symbol("#ALIGN16");
            add_builtin_symbol("#ALIGN16_NOP");
            add_builtin_symbol("#LONG_STRING_DATA");
            add_builtin_symbol("#IMPORT_RAW_DATA");
            add_builtin_symbol("#BIG_ENDIAN");
            add_builtin_symbol("#LITTLE_ENDIAN");
            //add_builtin_symbol("#SCOPE");

            for (int i = 0; i <= 7; i++)
            {
                add_register_symbol("R" + i + "_BANK", i, RegisterType.r_bank);
            }

            for (int i = 0; i <= 15; i++)
            {
                add_register_symbol("R" + i, i, RegisterType.r);
                add_register_symbol("FR" + i, i, RegisterType.fr);
                if (i % 2 == 0)
                {
                    add_register_symbol("XD" + i, i, RegisterType.xd);
                    add_register_symbol("DR" + i, i, RegisterType.dr);

                    if (i % 4 == 0)
                    {
                        add_register_symbol("FV" + i, i / 4, RegisterType.fv);
                    }
                }
            }

            // all them special registers
            // these first two have a special type because they are used in indirect addressing
            add_register_symbol("PC", -1, RegisterType.pc);
            add_register_symbol("GBR", -1, RegisterType.gbr);

            add_register_symbol("SR");
            add_register_symbol("SSR");
            add_register_symbol("SPC");
            add_register_symbol("VBR");
            add_register_symbol("SGR");
            add_register_symbol("DBR");
            add_register_symbol("MACH");
            add_register_symbol("MACL");
            add_register_symbol("PR");
            add_register_symbol("FPSCR");
            add_register_symbol("FPUL");
            add_register_symbol("XMTRX");
            add_register_symbol("MD");
            add_register_symbol("RB");
            add_register_symbol("BL");
            add_register_symbol("FD");
            add_register_symbol("IMASK");
            add_register_symbol("M");
            add_register_symbol("Q");
            add_register_symbol("S");
            add_register_symbol("T");
        }

        static void add_instruction_symbol(string name)
        {
            name = name.ToUpperInvariant();

            Symbol new_symbol = new Symbol();

            new_symbol.symbol_type = SymbolType.instruction;
            new_symbol.name = name;
            new_symbol.line_number = -1;
            new_symbol.statement_number = -1;
            new_symbol.size = 4;
            new_symbol.module = "$core";
            new_symbol.has_been_associated = true;

            symbol_table.Add(new_symbol.name, new_symbol);
        }

        static void add_builtin_symbol_alias(string alias_name, string target_name)
        {
            alias_name = alias_name.ToUpperInvariant();
            target_name = target_name.ToUpperInvariant();

            Symbol new_symbol = new Symbol();

            new_symbol.symbol_type = SymbolType.alias;
            new_symbol.alias_target = target_name;
            new_symbol.name = alias_name;
            new_symbol.line_number = -1;
            new_symbol.statement_number = -1;
            new_symbol.size = 4;
            new_symbol.module = "$core";
            new_symbol.has_been_associated = true;

            symbol_table.Add(new_symbol.name, new_symbol);
        }

        static void add_builtin_symbol(string name)
        {
            name = name.ToUpperInvariant();

            Symbol new_symbol = new Symbol();

            new_symbol.symbol_type = SymbolType.builtin;
            new_symbol.name = name;
            new_symbol.line_number = -1;
            new_symbol.statement_number = -1;
            new_symbol.size = 4;
            new_symbol.module = "$core";
            new_symbol.has_been_associated = true;

            symbol_table.Add(new_symbol.name, new_symbol);
        }

        static void add_register_symbol(string name, int value = -1, RegisterType register_type = RegisterType.other)
        {
            name = name.ToUpperInvariant();

            Symbol new_symbol = new Symbol();

            new_symbol.symbol_type = SymbolType.register;
            new_symbol.name = name;
            new_symbol.line_number = -1;
            new_symbol.statement_number = -1;
            new_symbol.value = value;
            new_symbol.register_type = register_type;
            new_symbol.module = "$core";
            new_symbol.has_been_associated = true;

            symbol_table.Add(new_symbol.name, new_symbol);
        }

        static void add_symbol(string name, long value, SymbolType symbol_type, char[] input_line, int line_number, int char_index, int statement_number, uint size, string module)
        {
            name = name.ToUpperInvariant();
            string name_with_module = module + "." + name;

            if (symbol_table.ContainsKey(name))
            {
                if (symbol_table[name].symbol_type != SymbolType.builtin
                    && symbol_table[name].symbol_type != SymbolType.instruction
                    && symbol_table[name].symbol_type != SymbolType.register
                    )
                {
                    Error(input_line, module, line_number, char_index,
                        "Attempt to redeclare symbol or label \""
                        + name_with_module
                        + "\" that already exists, was declared in module "
                        + symbol_table[name].module + ", line " + symbol_table[name].line_number);
                } else {
                    Error(input_line, module, line_number, char_index,
                        "Attempt to declare symbol or label \"" + name + "\", but that's already a builtin symbol, register, or instruction.");
                }
            }

            if (symbol_table.ContainsKey(name_with_module))
            {
                if (symbol_table[name_with_module].symbol_type != SymbolType.builtin
                    && symbol_table[name_with_module].symbol_type != SymbolType.instruction
                    && symbol_table[name_with_module].symbol_type != SymbolType.register
                    )
                {
                    Error(input_line, module, line_number, char_index,
                        "Attempt to redeclare symbol or label \""
                        + name_with_module
                        + "\" that already exists, was declared in module "
                        + symbol_table[name_with_module].module + ", line " + symbol_table[name_with_module].line_number);
                }
                else
                {
                    Error(input_line, module, line_number, char_index,
                        "Attempt to declare symbol or label \""
                        + name_with_module
                        + "\", but that's already a builtin symbol, register, or instruction.");
                }
            }

            Symbol new_symbol = new Symbol();
            new_symbol.name = name_with_module;
            new_symbol.short_name = name;
            new_symbol.value = value;
            new_symbol.symbol_type = symbol_type;
            new_symbol.line_number = line_number;
            new_symbol.statement_number = statement_number;
            new_symbol.size = size;
            new_symbol.module = module;
            new_symbol.has_been_associated = symbol_type != SymbolType.label;

            symbol_table.Add(new_symbol.name, new_symbol);
        }

        static List<Statement> tokenize_and_parse(StreamReader reader, string module, int statement_number_offset)
        {
            List<Statement> statements = new List<Statement>();


            int line_number = 1;
            string line = reader.ReadLine();
            while (line != null)
            {
                char[] line_chars = line.ToCharArray();

                Statement next_statement = tokenize_line(line_chars, line_number, statements.Count, module);

                if (next_statement != null)
                {
                    next_statement.module = module;

                    if (next_statement.instruction == "#LONG_STRING_DATA")
                    {
                        parse_multiline_string(reader, next_statement, ref line_number);
                    }

                    if (symbol_table.ContainsKey(next_statement.instruction)
                        && symbol_table[next_statement.instruction].symbol_type == SymbolType.alias)
                    {
                        next_statement.instruction = symbol_table[next_statement.instruction].alias_target;
                    }

                    statements.Add(next_statement);
                }

                line_number++;

                line = reader.ReadLine();
            }

            associate_labels(statements);

            return statements;
        } // tokenize_and_parse

        static void parse_multiline_string(StreamReader reader, Statement statement, ref int line_number)
        {
            StringBuilder sb = new StringBuilder();

            string line = reader.ReadLine();
            while (line != null)
            {
                if (line.StartsWith("#"))
                {
                    if (line.ToUpperInvariant().StartsWith("#END_LONG_STRING_DATA"))
                    {
                        Token data_token = new Token();
                        data_token.parse_type = ParseType.string_data;
                        data_token.raw_string = sb.ToString();

                        statement.tokens.Add(data_token);
                        statement.instruction = "#DATA";
                        line_number++;
                        return;
                    }
                }

                sb.Append(line);
                sb.Append('\n');

                line_number++;

                line = reader.ReadLine();
            }

            Error(statement.raw_line, statement.module, statement.line_number, -1,
                        "long string data was not properly ended with #END_LONG_STRING_DATA");
        }

        static Statement tokenize_line(char[] input_line, int line_number, int statement_number, string module)
        {
            int index = 0;
            // let's find our first token
            bool found = find_token(input_line, line_number, statement_number, ref index);

            if (!found)
            {
                return null;
            }

            Statement output = new Statement();
            output.tokens = new List<Token>();
            output.line_number = line_number;
            output.repeat_count = 1;

            {
                Token t = ReadSymbolOrLabel(input_line, line_number, statement_number, ref index, module);
                while (t != null && t.parse_type == ParseType.label_declaration && index < input_line.Length)
                {
                    t = ReadSymbolOrLabel(input_line, line_number, statement_number, ref index, module);
                }

                if (t == null || t.parse_type == ParseType.label_declaration)
                {
                    return null;
                }
                output.instruction = t.raw_string.ToUpperInvariant();
            }
            //Console.WriteLine(output.instruction);

            while (find_token(input_line, line_number, statement_number, ref index))
            {
                output.tokens.Add(ReadArgument(input_line, line_number, statement_number, ref index, module));

                //Console.WriteLine(output.tokens[output.tokens.Count - 1].raw_string);
            }

            output.raw_line = input_line;

            if (output.instruction == "#SYMBOL")
            {
                tokenize_symbol(input_line, line_number, statement_number, output, module);
            }

            return output;
        } // tokenize_line

        static void tokenize_symbol(char[] input_line, int line_number, int statement_number, Statement output, string module)
        {
            if (output.tokens.Count == 2)
            {
                if (output.tokens[0].parse_type != ParseType.name)
                {
                    Error(input_line, module, line_number, -1,
                        output.tokens[0].raw_string + " is not a valid " + output.instruction + " name");
                }

                long value = -1;
                uint size = 4;
                switch (output.tokens[1].parse_type)
                {
                    case ParseType.integer_number:
                        value = long.Parse(output.tokens[1].raw_string, CultureInfo.InvariantCulture);
                        size = 4;
                        break;
                    case ParseType.hex_number:
                        value = Convert.ToInt64(output.tokens[1].raw_string, 16);
                        size = (uint)(output.tokens[1].raw_string.Length - 2) / 2;
                        break;
                    case ParseType.expression:
                        assign_value_to_expression(output, output.tokens[1]);
                        value = output.tokens[1].value;
                        size = 4;
                        break;
                    case ParseType.float_number:
                        value = (long)BitConverter.ToUInt32(BitConverter.GetBytes(float.Parse(output.tokens[1].raw_string, CultureInfo.InvariantCulture)), 0);
                        size = 4;
                        break;
                    default:
                        Error(input_line, module, line_number, -1,
                            output.tokens[1].raw_string + " is not a valid " + output.instruction + " integer or float number");
                        break;
                }

                add_symbol(output.tokens[0].raw_string, value, SymbolType.from_symbol_directive, input_line, line_number, -1, statement_number, size, module);
            }
            else
            {
                Error(input_line, module, line_number, -1, output.tokens.Count + " is wrong number of inputs for " + output.instruction + " directive, need a name and a value");
            }
        }

        // FIXME code duplication with dasm
        static bool find_token(char[] input_line, int line_number, int statement_number, ref int index)
        {
            while(index < input_line.Length)
            {
                char c = input_line[index];

                switch (c)
                {
                    case ';':
                        return false; // whole line is a comment
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                    case ',':
                        index++;
                        break;
                    default:
                        return true;
                }
            }

            return false;
        } // findtoken

        static Token ReadArgument(char[] input_line, int line_number, int statement_number, ref int index, string module)
        {
            switch (input_line[index])
            {
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                case '-':
                    return ReadNumber(input_line, line_number, statement_number, ref index, module);
                case '/':
                case '.':
                case '#':
                case '_':
                    return ReadSymbol(input_line, line_number, statement_number, ref index, module);
                case '@':
                    return ReadIndirect(input_line, line_number, statement_number, ref index, module);
                case '"':
                    return ReadString(input_line, line_number, statement_number, ref index, module);
                case '{':
                    return ReadExpression(input_line, line_number, statement_number, ref index, module);
                default:
                    if (Char.IsLetter(input_line[index]))
                    {
                        return ReadSymbol(input_line, line_number, statement_number, ref index, module);
                    }

                    // FIXME better error message
                    Error(input_line, module, line_number, index, "unidentified token starts with '" + input_line[index] + "' (this may be an assembler bug)");
                    return null;
            }
        } // readtoken

        static Token ReadExpression(char[] input_line, int line_number, int statement_number, ref int index, string module)
        {
            if (expression_table == null)
            {
                expression_table = new List<Expression>();
            }

            Token output = new Token();
            StringBuilder sb = new StringBuilder();

            output.parse_type = ParseType.expression;

            if (input_line[index] != '{')
            {
                Error(input_line, module, line_number, index, "Assembler bug? Thought I was reading an expression but it started with: " + input_line[index]);
                return null;
            }
            sb.Append('{');
            index++;

            Expression expression = new Expression();
            expression.subtokens = new List<string>();
            expression.subtokens_type = new List<Expression.SubtokenType>();
            output.expression = expression;

            bool bContinueReading = true;
            while (bContinueReading && index < input_line.Length)
            {
                char c = input_line[index];

                switch (c)
                {
                    case '.':
                    case '_':
                        sb.Append(ReadExpressionSubtoken(input_line, line_number, statement_number, ref index, module, output));
                        sb.Append(c);
                        break;
                    case '+':
                        expression.subtokens.Add(c.ToString());
                        expression.subtokens_type.Add(Expression.SubtokenType.add);
                        sb.Append(c);
                        break;
                    case '-':
                        expression.subtokens.Add(c.ToString());
                        expression.subtokens_type.Add(Expression.SubtokenType.subtract);
                        sb.Append(c);
                        break;
                    case '*':
                        expression.subtokens.Add(c.ToString());
                        expression.subtokens_type.Add(Expression.SubtokenType.multiply);
                        sb.Append(c);
                        break;
                    case '/':
                        expression.subtokens.Add(c.ToString());
                        expression.subtokens_type.Add(Expression.SubtokenType.divide);
                        sb.Append(c);
                        break;
                    case '(':
                        expression.subtokens.Add(c.ToString());
                        expression.subtokens_type.Add(Expression.SubtokenType.open_parenthesis);
                        sb.Append(c);
                        break;
                    case ')':
                        expression.subtokens.Add(c.ToString());
                        expression.subtokens_type.Add(Expression.SubtokenType.close_parenthesis);
                        sb.Append(c);
                        break;
                    case '}':
                        bContinueReading = false;
                        sb.Append(c);
                        break;
                    case ' ':
                    case '\t':
                        sb.Append(c);
                        break;
                    default:
                        if (Char.IsLetterOrDigit(c))
                        {
                            sb.Append(ReadExpressionSubtoken(input_line, line_number, statement_number, ref index, module, output));
                            break;
                        }
                        else
                        {
                            Error(input_line, module, line_number, index,
                                "unexpected character '"
                                + c
                                + "' " + sb.ToString());
                        }
                        break;
                }

                index++;
            }

            if (bContinueReading)
            {
                Error(input_line, module, line_number, index, "unclosed expression starting with " + sb.ToString());
                return null;
            }

            output.raw_string = sb.ToString();

            expression_table.Add(expression);

            return output;
        } // readexpression

        static string ReadExpressionSubtoken
            (char[] input_line, int line_number,
            int statement_number, ref int index,
            string module, Token token)
        {
            StringBuilder sb = new StringBuilder();
            bool bContinueReading = true;

            if (Char.IsDigit(input_line[index]))
            {
                if (index + 1 < input_line.Length
                    && input_line[index] == '0'
                    && (input_line[index+1] == 'x' || input_line[index+1] == 'X'))
                {
                    token.expression.subtokens_type.Add(Expression.SubtokenType.hex_number);
                    sb.Append(input_line[index]);
                    index++;
                    sb.Append(input_line[index]);
                    index++;
                    
                    while (index < input_line.Length && bContinueReading)
                    {
                        char c = input_line[index];

                        if (Char.IsDigit(c))
                        {
                            sb.Append(c);
                            index++;
                        }
                        else if (c >= 'a' && c <= 'f'
                            || c >= 'A' && c <= 'F')
                        {
                            sb.Append(c);
                            index++;
                        }
                        else
                        {
                            index--;
                            bContinueReading = false;
                        }
                    }
                }
                else
                {
                    token.expression.subtokens_type.Add(Expression.SubtokenType.decimal_number);
                    
                    while (index < input_line.Length && bContinueReading)
                    {
                        char c = input_line[index];

                        if (Char.IsDigit(c))
                        {
                            sb.Append(c);
                            index++;
                        }
                        else
                        {
                            index--;
                            bContinueReading = false;
                        }
                    }
                }
            }
            else
            {
                token.expression.subtokens_type.Add(Expression.SubtokenType.name);

                while (index < input_line.Length && bContinueReading)
                {
                    char c = input_line[index];

                    // unicode symbol friendly hopefully!
                    if (Char.IsLetterOrDigit(c) || c == '.' || c == '_')
                    {
                        sb.Append(c);
                        index++;
                    }
                    else
                    {
                        index--;
                        bContinueReading = false;
                    }
                }
            }

            string output = sb.ToString();
            token.expression.subtokens.Add(output);

            return output;
        }// readexpressionsubtoken

        static Token ReadString(char[] input_line, int line_number, int statement_number, ref int index, string module)
        {
            Token output = new Token();
            StringBuilder sb = new StringBuilder();

            output.parse_type = ParseType.string_data;

            if (input_line[index] != '"')
            {
                Error(input_line, module, line_number, index, "Assembler bug? Thought I was reading a string but it started with: " + input_line[index]);
                return null;
            }
            index++;

            bool bContinueReading = true;
            while (bContinueReading && index < input_line.Length)
            {
                char c = input_line[index];

                switch (c)
                {
                    case '\\':
                        index++;
                        if (index < input_line.Length)
                        {
                            c = input_line[index];

                            switch (c)
                            {
                                case 't':
                                    sb.Append('\t');
                                    break;
                                case 'n':
                                    sb.Append('\n');
                                    break;
                                case '"':
                                case '\\':
                                    sb.Append(c);
                                    break;
                                default:
                                    Error(input_line, module, line_number, index, "unknown escape sequence '\\" + input_line[index] + "'. if you wanted literally the \\ followed by that letter, escape it with \\\\");
                                    return null;
                            }
                        }
                        break;
                    case '"':
                        bContinueReading = false;
                        break;
                    default:
                        sb.Append(c);
                        break;
                }

                index++;
            }

            if (bContinueReading)
            {
                Error(input_line, module, line_number, index, "unclosed string starting with " + sb.ToString());
                return null;
            }

            output.raw_string = sb.ToString();

            return output;
        } // readstring



        static Token ReadIndirect(char[] input_line, int line_number, int statement_number, ref int index, string module)
        {
            if (index >= input_line.Length - 1)
            {
                Error(input_line, module, line_number, index,
                    "expected indirect, found end of line");
                return null;
            }

            if (input_line[index] != '@')
            {
                Error(input_line, module, line_number, index,
                    "attempted to read indirect or absolute address starting with non-'@'. this is probably a bug with the assembler, please let us know.");
                return null;
            }
            index++;

            if (Char.IsNumber(input_line[index])
                && index+2 < input_line.Length
                && (input_line[index+1] == 'x' || input_line[index + 1] == 'X'))
            {
                Token abs = ReadNumber(input_line, line_number, statement_number, ref index, module);

                if (abs.parse_type == ParseType.hex_number)
                {
                    abs.parse_type = ParseType.absolute_displacement_address;
                }
                else
                {
                    Error(input_line, module, line_number, index, "absolute displacement address @" + abs.raw_string + " needs to be a hex number,\nand doesn't seem to be (start with @0x, etc)");
                }

                return abs;
            }

            Token output = new Token();
            StringBuilder sb = new StringBuilder();

            bool bExpectingComma = false;
            bool bExpectingCloseParenthesis = false;

            if (input_line[index] == '(')
            {
                sb.Append('(');
                bExpectingComma = true;
                bExpectingCloseParenthesis = true;
                index++;
            }
            else if (input_line[index] == '-')
            {
                sb.Append('-');
                output.parse_type = ParseType.register_indirect_pre_decrement;
                index++;
            }

            bool bContinueReading = true;

            while(index < input_line.Length && bContinueReading)
            {
                char c = input_line[index];

                switch (c)
                {
                    case ' ':
                    case '\t':
                        if (bExpectingCloseParenthesis || bExpectingComma)
                        {
                            sb.Append(c);
                            index++;
                        }
                        else
                        {
                            bContinueReading = false;
                        }
                        break;
                    case ';':
                    case '\r':
                    case '\n':
                        if (bExpectingCloseParenthesis)
                        {
                            Error(input_line, module, line_number, index, "Found unexpected whitespace when close parenthesis expected in indirect address");
                        }
                        bContinueReading = false;
                        break;
                    case '@':
                        bContinueReading = false;
                        break;
                    case '+':
                        sb.Append(c);
                        bContinueReading = false;
                        index++;
                        output.parse_type = ParseType.register_indirect_post_increment;
                        break;
                    case ')':
                        if (bExpectingCloseParenthesis)
                        {
                            sb.Append(c);
                            bContinueReading = false;
                            index++;
                            bExpectingCloseParenthesis = false;
                        }
                        else
                        {
                            Error(input_line, module, line_number, index, "Found unexpected ')' inside an indirect address argument (starts with '@')");
                        }
                        break;
                    case ',':
                        if (bExpectingComma)
                        {
                            sb.Append(c);
                            bExpectingCloseParenthesis = true;
                            index++;
                        }
                        else
                        {
                            if (bExpectingCloseParenthesis)
                            {
                                Error(input_line, module, line_number, index, "Found unexpected ',' when close parenthesis expected in indirect");
                            }
                            bContinueReading = false;
                        }
                        break;
                    case ':':
                        Error(input_line, module, line_number, index, "Found unexpected ':' inside an indirect address argument (starts with '@')");

                        bContinueReading = false;
                        break;
                    case '.':
                    default:
                        if (Char.IsDigit(c))
                        {
                            if (output.inner_token2 != null)
                            {
                                Error(input_line, module, line_number, index, "no indirect address argument accepts more than 2 values");
                            }
                            else if (output.inner_token != null)
                            {
                                Error(input_line, module, line_number, index, "no indirect address argument accepts numeric tokens for the 2nd value");
                            }

                            output.parse_type = ParseType.register_indirect_displacement;
                            output.inner_token = ReadNumber(input_line, line_number, statement_number, ref index, module);

                            sb.Append(output.inner_token.raw_string);
                            bExpectingComma = true;
                        }
                        else if (Char.IsLetter(c))
                        {
                            if (output.inner_token2 != null)
                            {
                                Error(input_line, module, line_number, index, "no indirect address argument accepts more than 2 values");
                            }
                            else if (output.inner_token != null)
                            {
                                output.inner_token2 = ReadSymbol(input_line, line_number, statement_number, ref index, module);

                                sb.Append(output.inner_token2.raw_string);
                                bExpectingComma = false;
                                bExpectingCloseParenthesis = true;

                                switch (output.inner_token2.parse_type)
                                {
                                    case ParseType.register_direct:
                                        if (output.inner_token.parse_type == ParseType.register_direct)
                                        {
                                            output.parse_type = ParseType.register_indexed_indirect;
                                        }
                                        else if (output.inner_token.parse_type == ParseType.name
                                            || output.inner_token.parse_type == ParseType.integer_number
                                            || output.inner_token.parse_type == ParseType.hex_number)
                                        {
                                            output.parse_type = ParseType.register_indirect_displacement;
                                        }
                                        break;
                                    case ParseType.pc_register:
                                        if (output.inner_token.parse_type == ParseType.name
                                           || output.inner_token.parse_type == ParseType.integer_number
                                           || output.inner_token.parse_type == ParseType.hex_number)
                                        {
                                            output.parse_type = ParseType.pc_displacement;
                                        }
                                        else if (output.inner_token.parse_type == ParseType.register_direct)
                                        {
                                            Error(input_line, module, line_number, index, "can't index PC with registers. (" + output.inner_token.raw_string + ")");
                                        }
                                        else
                                        {
                                            Error(input_line, module, line_number, index, "can't index or displace PC with " + output.inner_token.raw_string);
                                        }
                                        break;
                                    case ParseType.gbr_register:
                                        if (output.inner_token.parse_type == ParseType.name
                                           || output.inner_token.parse_type == ParseType.integer_number
                                           || output.inner_token.parse_type == ParseType.hex_number)
                                        {
                                            output.parse_type = ParseType.gbr_indirect_displacement;
                                        }
                                        else if (output.inner_token.parse_type == ParseType.register_direct)
                                        {
                                            if (symbol_table[output.inner_token.raw_string.ToUpperInvariant()].value == 0)
                                            {
                                                output.parse_type = ParseType.gbr_indirect_indexed;
                                            }
                                            else
                                            {
                                                Error(input_line, module, line_number, index, "can't index GBR with registers that aren't R0, was " + output.inner_token.raw_string);
                                            }
                                        }
                                        else
                                        {
                                            Error(input_line, module, line_number, index, "can't index or displace GBR with " + output.inner_token.raw_string);
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                output.inner_token = ReadSymbol(input_line, line_number, statement_number, ref index, module);

                                sb.Append(output.inner_token.raw_string);

                                if (output.parse_type != ParseType.register_indirect_pre_decrement)
                                {
                                    if (output.inner_token.parse_type == ParseType.name)
                                    {
                                        output.parse_type = ParseType.pc_displacement;
                                    } else {
                                        output.parse_type = ParseType.register_indirect_displacement;

                                        if (bExpectingCloseParenthesis)
                                        {
                                            bExpectingComma = true;
                                        }
                                        else
                                        {
                                            output.parse_type = ParseType.register_indirect;
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            Error(input_line, module, line_number, index, "was trying to read an indirect (starts with '@" + sb.ToString() + "'), but found this " + c + " in the middle of it");
                        }
                        break;
                }
            } // for

            output.raw_string = sb.ToString();

            return output;
        } // readrelative

        static Token ReadNumber(char[] input_line, int line_number, int statement_number, ref int index, string module)
        {
            Token output = new Token();
            StringBuilder sb = new StringBuilder();

            output.parse_type = ParseType.integer_number;

            if (
                index < input_line.Length - 1
                && input_line[index] == '0'
                &&
                (input_line[index + 1] == 'x' || input_line[index + 1] == 'x')
                )
            {
                output.parse_type = ParseType.hex_number;

                sb.Append(input_line[index]);
                sb.Append(input_line[index + 1]);

                index += 2;
            }
            else if(index < input_line.Length - 1
                && input_line[index] == '-')
            {
                sb.Append('-');
                index++;
            }

            bool bContinueReading = true;

            while(index < input_line.Length && bContinueReading)
            {
                char c = input_line[index];

                switch (c)
                {
                    case ';':
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                    case '@':
                    case ',':
                    case '(':
                    case ')':
                        bContinueReading = false;
                        break;
                    case ':':
                        Error(input_line, module, line_number, index, "Found a ':', but this doesn't appear to be a valid label? but instead a number?? ");

                        bContinueReading = false;
                        break;
                    case '.':
                        if (output.parse_type == ParseType.integer_number)
                        {
                            output.parse_type = ParseType.float_number;
                        }
                        sb.Append(c);
                        index++;
                        break;
                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        sb.Append(c);
                        index++;
                        break;
                    case 'a':
                    case 'b':
                    case 'c':
                    case 'd':
                    case 'e':
                    case 'f':
                    case 'A':
                    case 'B':
                    case 'C':
                    case 'D':
                    case 'E':
                    case 'F':
                        if (output.parse_type == ParseType.hex_number)
                        {
                            sb.Append(c);
                            index++;
                        } else {
                            Error(input_line, module, line_number, index, "hex numbers need 0x at the start, but there was a " + c + " in this number? ");
                        }
                        break;
                    default:
                        Error(input_line, module, line_number, index, "was trying to read a number in, but found this " + c + " in the middle of it");
                        break;
                }
            } // while

            output.raw_string = sb.ToString();

            return output;
        } // read number

        // note to self: figure out why this is commented out
        // read register
        /*
        static Token ReadRegister(char[] input_line, ref int index)
        {
            // R0 to R15
            // FR0 to FR15
            // XF0 to XF15
            Token output = new Token();
            StringBuilder sb = new StringBuilder();

            if (index == input_line.Length - 1)
            {
                Error(input_line, line_number, index, "Was trying to read in a register but then the file ends ");
            }

            switch (input_line[index])
            {
                case 'R':
                case 'r':
                    output.parse_type = ParseType.register_direct;
                    break;
                case 'D':
                case 'd':
                    output.parse_type = ParseType.dr_register_direct;
                    break;
                case 'F':
                case 'f':
                    output.parse_type = ParseType.fr_register_direct;
                    index++;
                    switch(input_line[index])
                    {
                        case 'v':
                        case 'V':
                            output.parse_type = ParseType.fv_register_direct;
                            break;
                        case 'r':
                        case 'R':
                            break;
                        default:
                            Error(input_line, line_number, index, "Register must start with 'R','DR','FR', or 'XD', but it was " + input_line[index-1] + input_line[index] + " instead");
                            return null;
                    }
                    break;
                case 'x':
                case 'X':
                    output.parse_type = ParseType.xd_register_direct;
                    index++;
                    switch (input_line[index])
                    {
                        case 'd':
                        case 'D':
                            break;
                        default:
                            Error(input_line, line_number, index, "Register must start with 'R','DR','FR', or 'XD', but it was " + input_line[index - 1] + input_line[index] + " instead");
                            return null;
                    }
                    break;
                default:
                    Error(input_line, line_number, index, "Register must start with 'R','DR','FR', or 'XD', but it was " + input_line[index] + " instead");
                    return null;
            }

            index++;

            switch (input_line[index])
            {
                case '0':
                case '1':
                case '4':
                case '8':
                    sb.Append(input_line[index]);
                    break;
                case '2':
                case '6':
                    if (output.parse_type == ParseType.fv_register_direct)
                    {
                        Error(input_line, line_number, index, "FV registers are only valid as FV0, FV4, FV8, or FV12");
                    }
                    sb.Append(input_line[index]);
                    break;
                case '3':
                case '5':
                case '7':
                case '9':
                    if (output.parse_type == ParseType.fv_register_direct)
                    {
                        Error(input_line, line_number, index, "FV registers are only valid as FV0, FV4, FV8, or FV12");
                    }

                    if (output.parse_type == ParseType.dr_register_direct)
                    {
                        Error(input_line, line_number, index, "DR registers are only valid as DR0, DR2, DR4, DR6, DR8, DR10, DR12, DR14");
                    }

                    if (output.parse_type == ParseType.xd_register_direct)
                    {
                        Error(input_line, line_number, index, "XD registers are only valid as XD0, XD2, XD4, XD6, XD8, XD10, XD12, XD14");
                    }

                    sb.Append(input_line[index]);
                    break;
                default:
                    Error(input_line, line_number, index, "Register needs a number, but it was " + input_line[index] + " instead");
                    return null;
            }

            output.raw_string = sb.ToString();

            return output;
        } // readregister
        */

        static Token ReadSymbolOrLabel(char[] input_line, int line_number, int statement_number, ref int index, string module)
        {
            Token output = ReadSymbol(input_line, line_number, statement_number, ref index, module);

            if (index < input_line.Length && input_line[index] == ':')
            {
                if (output.parse_type != ParseType.name)
                {
                    Error(input_line, module, line_number, index, "Invalid label \"" + output.raw_string + "\"");
                }
                add_symbol(output.raw_string, statement_number, SymbolType.label, input_line,line_number,index, statement_number, 4, module);

                index++;
                output.parse_type = ParseType.label_declaration;
                output.raw_string += ":";
            }

            return output;
        }

        static Token ReadSymbol(char[] input_line, int line_number, int statement_number, ref int index, string module)
        {
            Token output = new Token();
            StringBuilder sb = new StringBuilder();

            output.parse_type = ParseType.name;

            if (Char.IsNumber(input_line[index]))
            {
                Error(input_line, module, line_number, index, "Symbols, instructions, or label names cannot start with numbers, but started with " + input_line[index]);
                return null;
            }

            if (input_line[index] == '#')
            {
                sb.Append(input_line[index]);
                index++;
            }

            bool bContinueReading = true;
            while(index < input_line.Length && bContinueReading)
            {
                char c = input_line[index];
                
                // unicode symbol friendly hopefully!
                if (Char.IsLetterOrDigit(c) || c == '.' || c == '/' || c == '_')
                {
                    sb.Append(c);
                    index++;
                }
                else
                {
                    bContinueReading = false;
                }
            }

            output.raw_string = sb.ToString();

            string key = output.raw_string.ToUpperInvariant();
            if (symbol_table.ContainsKey(key))
            {
                Symbol potential_register = symbol_table[key];
                if (potential_register.symbol_type == SymbolType.register)
                {
                    output.value = potential_register.value;

                    switch (potential_register.register_type)
                    {
                        case RegisterType.dr:
                            output.parse_type = ParseType.dr_register_direct;
                            break;
                        case RegisterType.r:
                            output.parse_type = ParseType.register_direct;
                            break;
                        case RegisterType.xd:
                            output.parse_type = ParseType.xd_register_direct;
                            break;
                        case RegisterType.fv:
                            output.parse_type = ParseType.fv_register_direct;
                            break;
                        case RegisterType.pc:
                            output.parse_type = ParseType.pc_register;
                            break;
                        case RegisterType.gbr:
                            output.parse_type = ParseType.gbr_register;
                            break;
                        case RegisterType.fr:
                            output.parse_type = ParseType.fr_register_direct;
                            break;
                        case RegisterType.r_bank:
                            output.parse_type = ParseType.r_bank_register_direct;
                            break;
                        default:
                            output.parse_type = ParseType.other_register;
                            break;
                    }
                }
            }

            return output;
        } // readsymbol

        static void Error(char[] input_line, string module, int line_number, int char_index, string message)
        {
            Warn(input_line, module, line_number, char_index, message, "Error:\n");

            // FIXME TODO
            Console.WriteLine();
            Console.WriteLine();
            throw new Exception("NO ERROR HANDLING YET SORRY");
        }

        static void Warn(char[] input_line, string module, int line_number, int char_index, string message, string prepend = "Warning:\n")
        {
            char_index++;
            Console.Write(prepend);
            Console.WriteLine(message);

            if (char_index - 2 > 0)
            {
                Console.Write("\ton line number " + line_number + " col " + char_index);
                if (!string.IsNullOrEmpty(module))
                {
                    Console.Write(" in module ");
                    Console.Write(module);
                }
                Console.WriteLine(":");
                Console.WriteLine(input_line);
                StringBuilder sb = new StringBuilder();
                sb.Append(' ', char_index - 2);
                Console.WriteLine(sb.ToString() + "^");
                Console.WriteLine(sb.ToString() + "|");
                Console.WriteLine(sb.ToString() + "|");
            }
            else
            {
                Console.Write("\ton line number " + line_number);
                if (!string.IsNullOrEmpty(module))
                {
                    Console.Write(" in module ");
                    Console.Write(module);
                }
                Console.WriteLine(":");
                Console.WriteLine(input_line);
            }

            // FIXME TODO
            Console.WriteLine();
            Console.WriteLine();
            throw new Exception("NO ERROR HANDLING YET SORRY");
        }


        static void handle_module_loading(List<Statement> input, List<Statement> output, int statement_offset)
        {
            foreach (Statement s in input)
            {
                if (s.instruction == "#MODULE")
                {
                    if (s.tokens[0].parse_type == ParseType.string_data)
                    {
                        string module_name = Path.GetFileNameWithoutExtension(s.tokens[0].raw_string).ToUpperInvariant();
                        if (modules_loaded.ContainsKey(module_name))
                        {
                            Error(null, s.module, s.line_number, 0, "");
                        }

                        string module_path = Path.Combine(working_directory, s.tokens[0].raw_string);

                        Module new_module = new Module();
                        new_module.name = module_name;
                        new_module.statement_number_offset = output.Count + statement_offset;
                        modules_loaded.Add(module_name, new_module);

                        using (StreamReader reader = File.OpenText(module_path))
                        {
                            List<Statement> module_statements = tokenize_and_parse(reader, module_name, output.Count + statement_offset);
                            List<Statement> module_statements_handled = new List<Statement>();
                            handle_module_loading(module_statements, module_statements_handled, output.Count + statement_offset);

                            output.AddRange(module_statements_handled);
                        }
                    }
                }
                else
                {
                    output.Add(s);
                }
            }
        } // handle_module_loading

        static void associate_labels(List<Statement> statements)
        {
            foreach (Symbol l in symbol_table.Values)
            {
                if (!l.has_been_associated
                    && l.symbol_type == SymbolType.label
                    && statements.Count > l.statement_number
                    && l.statement_number >= 0)
                {
                    if (statements[l.statement_number].associated_labels == null)
                    {
                        statements[l.statement_number].associated_labels = new List<Symbol>();
                    }
                    statements[l.statement_number].associated_labels.Add(l);
                    l.has_been_associated = true;
                }
            }
        } // associate_labels

        static void fix_associated_labels(List<Statement> statements)
        {
            for (int statement_index = 0; statement_index < statements.Count; statement_index++)
            {
                Statement s = statements[statement_index];

                if (s.associated_labels != null)
                {
                    foreach (Symbol label in s.associated_labels)
                    {
                        label.statement_number = statement_index; //+ modules_loaded[s.module].statement_number_offset;
                    }
                }
            }
        }
    } // class
} // ns






