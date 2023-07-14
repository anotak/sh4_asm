using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Globalization;

namespace sh4_disasm
{
    public class Program
    {
        enum eCodeCheckStatus
        {
            Unknown,
            Failed,
            Success
        }

        class SH4Word
        {
            public bool is_already_jumped;
            public eCodeCheckStatus code_check_status;
            public bool is_data;
            public bool is_double_word_data;
            public bool is_byte_data;
            public bool is_align4;
            public InstructionTemplate template;
            public ushort raw_value;
            public List<int> args; // fixme this sucks use indices that are set in the template
            public string comment;
            public int repeat_count;
        }

        class InstructionTemplate
        {
            public List<string> tokens;
            public ushort and_mask; // first input will be &ed with this, to remove arguments
            public ushort check; // then checked against this

            //public Dictionary<string, ushort> arg_masks;
            //public Dictionary<string, int> arg_shifts; 
            public Dictionary<string, ArgumentTemplate> args;

            public int displace_size;
            public bool is_jump;
            public bool is_return;
            public bool is_call;
            public bool is_priv;
            public bool is_register_indirect_jump;
            public bool is_delayed;
            public bool is_pc_disp_load;
            public bool is_signed_displacement;
            public bool is_no_add_disp;
            public bool is_no_mutate;
        }

        class ArgumentTemplate
        {
            public ushort mask; // input instruction will be &ed with this to find arg
            public int shift;// arg will be >>ed by this after mask
            public bool is_register;
            public int index; // in args of SH4Word
            public int size;
        }

        class Label
        {
            public int index;
            public uint address;
            public string name;
            public string module;
            public bool is_name_final;
            public bool is_used;
            public bool is_function;
            public bool is_code;
            public bool is_local;

            // function call state leading up to it
            public Dictionary<int,HashSet<uint>> reg_possible_values;
            public HashSet<uint> loaded_values_before;

            public Label()
            {
                index = -1;
                address = 0;
                name = "";
                module = "";
                is_name_final = false;
                is_used = false;
                is_function = false;
                is_code = false;
                is_local = false;
            }

            public void add_execution_state(ExecutionState state)
            {
                if (state.loaded_values.Count > 0)
                {
                    if (loaded_values_before == null)
                    {
                        loaded_values_before = new HashSet<uint>();
                    }
                    loaded_values_before.UnionWith(state.loaded_values);
                }

                reg_possible_values = new Dictionary<int, HashSet<uint>> ();

                for (int index = 0; index < 16; index++)
                {
                    if (!reg_possible_values.ContainsKey(index) && state.reg_accessed[index])
                    {
                        reg_possible_values.Add(index, new HashSet<uint>());
                    }

                    if (state.reg_set[index])
                    {
                        reg_possible_values[index].Add(state.reg_value[index]);
                    }
                }
            }
        }

        class ExecutionState
        {
            public bool[] reg_set;
            public bool[] reg_accessed;
            public uint[] reg_value;
            public HashSet<uint> loaded_values;

            public ExecutionState()
            {
                reg_set = new bool[16];
                reg_accessed = new bool[16];
                reg_value = new uint[16];
                loaded_values = new HashSet<uint>();
            }
        }

        // key is where it points
        // value is where in input file we found it
        // todo - figure out if we should use starting_offset or not here
        static Dictionary<uint, List<uint>> probable_addresses;

        // key is address
        static Dictionary<uint, Label> labels;

        static List<InstructionTemplate> templates;

        // FIXME make a struct to hold all this info
        static uint starting_offset;
        static uint ending_offset;
        // for if you want to only disassemble part of a file
        static uint starting_offset_within_file;
        static uint ending_offset_within_file;
        static List<uint> entry_points;
        static bool is_code_kernel_mode = false;
        static bool is_comments_enabled = true;

        static void Main(string[] args)
        {
            starting_offset = 0xce30000;
            starting_offset_within_file = 0x0;

            if (args.Length < 2)
            {
                Console.WriteLine("need input filename, output filename.\nthird option is offset, is optional");
                Console.WriteLine("optional --kernelmode to allow privileged instrtuctions");
                return;
            }
            entry_points = new List<uint>();

            string stats_filename = null;

            if (args.Length >= 3)
            {
                args[2] = args[2].ToUpperInvariant();

                // fixme error properly on invalid offset
                if (args[2].StartsWith("0X"))
                {
                    starting_offset = Convert.ToUInt32(args[2], 16);
                }
                else
                {
                    starting_offset = UInt32.Parse(args[2], CultureInfo.InvariantCulture);
                }

                // FIXME this sucks right now
                for (int arg_index = 3; arg_index < args.Length; arg_index++)
                {
                    switch (args[arg_index].ToLowerInvariant())
                    {
                        case "--nocomment":
                            is_comments_enabled = false;
                            break;
                        case "--kernelmode":
                            is_code_kernel_mode = true;
                            break;
                        case "--entrypoint":
                            arg_index++;
                            if (arg_index < args.Length)
                            {
                                if (args[arg_index].ToUpperInvariant().StartsWith("0X"))
                                {
                                    entry_points.Add(Convert.ToUInt32(args[arg_index], 16));
                                }
                                else
                                {
                                    entry_points.Add(UInt32.Parse(args[arg_index], CultureInfo.InvariantCulture));
                                }
                            }
                            else
                            {
                                Console.WriteLine("--entrypoint with no number after?");
                                return;
                            }
                            break;
                        case "--range":
                            {
                                arg_index++;
                                if (arg_index < args.Length)
                                {
                                    args[arg_index] = args[arg_index].Replace("loc_", "0x");
                                    if (args[arg_index].ToUpperInvariant().StartsWith("0X"))
                                    {
                                        starting_offset_within_file = Convert.ToUInt32(args[arg_index], 16);
                                    }
                                    else
                                    {
                                        starting_offset_within_file = UInt32.Parse(args[arg_index], CultureInfo.InvariantCulture);
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("--range with no numbers after?");
                                    return;
                                }

                                arg_index++;
                                if (arg_index < args.Length)
                                {
                                    args[arg_index] = args[arg_index].Replace("loc_", "0x");
                                    if (args[arg_index].ToUpperInvariant().StartsWith("0X"))
                                    {
                                        ending_offset_within_file = Convert.ToUInt32(args[arg_index], 16);
                                    }
                                    else
                                    {
                                        ending_offset_within_file = UInt32.Parse(args[arg_index], CultureInfo.InvariantCulture);
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("--range with only 1 number after?");
                                    return;
                                }
                            }
                            break;
                        case "--symboltable":
                            arg_index++;
                            if (arg_index < args.Length)
                            {
                                if (!load_symbol_table(args[arg_index]))
                                {
                                    Console.WriteLine("Error reading symbol table, terminating");
                                    return;
                                }
                            }
                            else
                            {
                                Console.WriteLine("--symboltable with no filename after?");
                                return;
                            }
                            break;
                        case "--stats":
                            arg_index++;
                            if (arg_index < args.Length)
                            {
                                stats_filename = args[arg_index];
                            }
                            else
                            {
                                Console.WriteLine("--stats with no filename after?");
                                return;
                            }
                            break;
                        default:
                            // TODO print usage etc
                            Console.WriteLine("unknown argument " + args[arg_index]);
                            return;
                    }
                }
            }

            Console.WriteLine("disasm using offset 0x" + starting_offset.ToString("X2"));

            string instructions_path =
                Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "instructions.data");

            load_instruction_templates(instructions_path);

            StringBuilder sb = disasm(args[0]);

            string temp_filename = Path.GetTempFileName();

            File.WriteAllText(temp_filename, sb.ToString());

            File.Delete(args[1] + ".bak");

            if (File.Exists(args[1]))
            {
                File.Copy(args[1], args[1] + ".bak");
            }

            File.Delete(args[1]);
            File.Copy(temp_filename, args[1]);

            if (stats_filename != null)
            {
                save_stats(stats_filename);
            }
        } // main


        // FIXME THIS SUX
        static StringBuilder disasm(string filename)
        {
            int filesize;
            if (labels == null)
            {
                labels = new Dictionary<uint, Label>();
            }
            List<SH4Word> words = new List<SH4Word>();

            //Console.WriteLine("reading " + filename);
            using (BinaryReader reader = new BinaryReader(File.Open(filename, FileMode.Open)))
            {
                filesize = (int)reader.BaseStream.Length;

                if (starting_offset_within_file == 0)
                {
                    starting_offset_within_file = starting_offset;
                }
                
                ending_offset = starting_offset + (uint)filesize;

                long end_pos = reader.BaseStream.Length;

                if (ending_offset_within_file == 0)
                {
                    ending_offset_within_file = ending_offset;
                }

                if (starting_offset_within_file != starting_offset || ending_offset_within_file != ending_offset)
                {
                    ending_offset = ending_offset_within_file;
                    reader.BaseStream.Seek(starting_offset_within_file - starting_offset, SeekOrigin.Begin);

                    starting_offset = starting_offset_within_file;

                    end_pos = (ending_offset - starting_offset) + reader.BaseStream.Position;

                    Console.WriteLine("only doing range of 0x" + starting_offset.ToString("X2") + " to 0x" + ending_offset.ToString("X2"));
                }

                bool bReadExtraByte = false;
                if (end_pos % 2 == 1)
                {
                    end_pos--;
                    bReadExtraByte = true;
                }

                while (reader.BaseStream.Position < end_pos)
                {
                    ushort insn = reader.ReadUInt16();

                    words.Add(read_insn(insn));
                }

                if (bReadExtraByte)
                {
                    ushort insn = (ushort)(reader.ReadByte() << 8);

                    words.Add(read_insn(insn));
                }
            }

            find_probable_addresses(words);

            try_to_add_label_at_index(0);
            if (is_comments_enabled)
            {
                labels[starting_offset].name = "start_" + starting_offset.ToString("X2");
            }
            labels[starting_offset].is_name_final = true;
            follow_code_flow(words, 0);

            foreach (uint address in entry_points)
            {
                if (address >= starting_offset
                    && address < ending_offset
                    && address % 2 == 0)
                {
                    int addr_index = (int)(((long)address - (long)starting_offset) / 2);

                    bool is_code = check_code_flow(words, addr_index);
                    if (is_code)
                    {
                        follow_code_flow(words, addr_index);
                    }
                    else
                    {
                        Console.WriteLine("entry point 0x" + address.ToString("X8") + "resulted in non-code?? ignoring");
                    }
                }
                else
                {
                    // TODO specify why bad
                    Console.WriteLine("entry point out of range or not divisible by 2: 0x" + address.ToString("X8"));
                }
            }

            foreach (uint address in probable_addresses.Keys)
            {
                if (address >= starting_offset
                    && address < ending_offset
                    && address % 2 == 0)
                {
                    int addr_index = (int)(((long)address - (long)starting_offset) / 2);
                    bool is_code = check_code_flow(words, addr_index);

                    if (is_code)
                    {
                        /*Console.WriteLine("following pointer to " + address.ToString("X8"));

                        foreach (uint source in probable_addresses[address])
                        {
                            Console.WriteLine("\tfrom " + source.ToString("X8"));
                        }*/

                        follow_code_flow(words, addr_index);
                    }
                }

                foreach (uint source in probable_addresses[address])
                {
                    int index = (int)(source - starting_offset);

                    while (index < words.Count - 1
                        && words[index].is_data
                        && words[index + 1].is_data)
                    {
                        words[index].is_double_word_data = true;
                        words[index + 1].is_double_word_data = true;

                        index += 2;
                    }
                }
            }

            rename_labels(words);

            clean_up_nop_padding(words);

            StringBuilder sb = new StringBuilder();

            if (is_comments_enabled)
            {
                sb.Append("; ");
                sb.Append(Path.GetFileName(filename));
                sb.Append("\n");
            }

            build_text_output(words,sb);

            //Console.WriteLine(sb.ToString());
            return sb;
        }

        // replace #data 0x0009 runs with NOPs
        static void clean_up_nop_padding(List<SH4Word> words)
        {
            bool bPreviousImpliesNop = true;

            // how many nops have we encountered, we merge too many nops with #repeat
            int nop_run_count = 0;

            int nop_run_first = 0;

            for (int i = 0; i < words.Count; i++)
            {
                SH4Word word = words[i];

                bool bIsNop = false;

                if (word.is_double_word_data || word.is_byte_data)
                {
                    bPreviousImpliesNop = true;
                }
                else if (word.is_data)
                {
                    if (bPreviousImpliesNop && word.raw_value == 0x0009)
                    {
                        uint addr = index_to_address(i);
                        if (labels.ContainsKey(addr))
                        {
                            Label label = labels[addr];

                            if (label.is_code || label.is_function)
                            {
                                if (nop_run_count > 3)
                                {
                                    words[nop_run_first].repeat_count = nop_run_count;
                                }

                                nop_run_first = i;
                                nop_run_count = 0;

                                bIsNop = true;
                            }
                            else
                            {
                                bPreviousImpliesNop = false;
                            }
                        }
                        else
                        {
                            bIsNop = true;
                        }
                    }
                    else
                    {
                        bPreviousImpliesNop = false;
                    }
                }
                else if (!word.is_data)
                {
                    bPreviousImpliesNop = true;
                }

                if (bIsNop)
                {
                    word.is_data = false;

                    if (nop_run_count == 0)
                    {
                        nop_run_first = i;
                    }

                    nop_run_count++;
                } else {
                    if (nop_run_count > 3)
                    {
                        words[nop_run_first].repeat_count = nop_run_count;
                    }

                    nop_run_count = 0;
                }
            } // for words

            if (nop_run_count > 3)
            {
                words[nop_run_first].repeat_count = nop_run_count;
            }

            nop_run_count = 0;
        } // clean_up_nop_padding

        static bool load_symbol_table(string filename)
        {
            if (labels == null)
            {
                labels = new Dictionary<uint, Label>();
            }

            if (Path.GetExtension(filename).ToLowerInvariant() == ".asm")
            {
                using (StreamReader reader = File.OpenText(filename))
                {
                    int line_number = 1;

                    string line = reader.ReadLine();
                    while (line != null)
                    {
                        char[] line_chars = line.ToCharArray();

                        if (!load_symbol_line(line_chars, line_number))
                        {
                            Console.WriteLine("load symbols from " + filename + " failed");
                            return false;
                        }

                        line_number++;

                        line = reader.ReadLine();
                    }
                }
            }
            else
            {
                using (BinaryReader reader = new BinaryReader(File.Open(filename, FileMode.Open)))
                {
                    return load_symbol_table_binary(reader);
                }
            }
            return true;
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
                // module not used here for now
                //sb.Append(modules[module_index]);
                //sb.Append(".");
                sb.Append(reader.ReadString());

                string symbol_name = sb.ToString();
                uint symbol_value = reader.ReadUInt32();

                Label l = new Label();
                l.is_name_final = true;
                l.module = modules[module_index].ToLowerInvariant();
                l.name = symbol_name.ToLowerInvariant();
                l.address = symbol_value;

                if (labels.ContainsKey(l.address))
                {
                    Console.WriteLine("duplicate: " + symbol_name + " with value " + symbol_value.ToString("X2"));
                }

                labels.Add(l.address, l);
            }

            return true;
        }

        // FIXME code duplication with asm
        static bool find_token(char[] input_line, ref int index)
        {
            while (index < input_line.Length)
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

        // FIXME code duplication with ASM
        static string read_symbol_name(char[] input_line, ref int index)
        {
            StringBuilder sb = new StringBuilder();

            if (Char.IsNumber(input_line[index]))
            {
                Console.WriteLine("Symbols, instructions, or label names cannot start with numbers, but started with " + input_line[index]);
                return null;
            }

            if (input_line[index] == '#')
            {
                sb.Append(input_line[index]);
                index++;
            }

            bool bContinueReading = true;
            while (index < input_line.Length && bContinueReading)
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

            return sb.ToString().ToLowerInvariant();
        } // read_symbol_name

        // FIXME code duplication with ASM
        static string ReadNumber(char[] input_line, ref int index)
        {
            StringBuilder sb = new StringBuilder();

            bool is_hex = false;
            if (
                index < input_line.Length - 1
                &&
                (input_line[index + 1] == 'x' || input_line[index + 1] == 'x')
                )
            {
                sb.Append(input_line[index]);
                sb.Append(input_line[index + 1]);

                index += 2;
                is_hex = true;
            }


            bool bContinueReading = true;

            while (index < input_line.Length && bContinueReading)
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
                        Console.WriteLine("Found a ':', but this doesn't appear to be a valid label? but instead a number?? ");

                        return null;
                    case '.':
                        Console.WriteLine("Found a '.' but floats are not supported in symbol table ");

                        return null;
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
                        if (is_hex)
                        {
                            sb.Append(c);
                            index++;
                        }
                        else
                        {
                            Console.WriteLine("hex numbers need 0x at the start, but there was a " + c + " in this number? ");
                            return null;
                        }
                        break;
                    default:
                        Console.WriteLine("was trying to read a number in, but found this " + c + " in the middle of it");
                        return null;
                }
            } // while

            return sb.ToString();
        } // read number

        // true on success, false on error
        static bool load_symbol_line(char[] input_line, int line_number)
        {
            int index = 0;
            int in_len = input_line.Length;

            {
                bool bFound = find_token(input_line, ref index);
                
                if (!bFound)
                {
                    // blank line
                    return true;
                }
            }
            
            if (index + "#SYMBOL".Length > in_len)
            {
                return false;
            }

            {
                StringBuilder sb = new StringBuilder(10);
                index++;

                int target_index = index + "#SYMBOL".Length;
                bool bDone = false;
                while (index < target_index && !bDone)
                {
                    char c = input_line[index];
                    
                    switch (c)
                    {
                        case '#':
                        case 's':
                        case 'y':
                        case 'm':
                        case 'b':
                        case 'o':
                        case 'l':
                        case 'S':
                        case 'Y':
                        case 'M':
                        case 'B':
                        case 'O':
                        case 'L':
                            sb.Append(c);
                            index++;
                            break;
                        case ' ':
                        case '\t':
                        case '\r':
                        case '\n':
                        case ',':
                            bDone = true;
                            index++;
                            break;
                        default:
                            Console.WriteLine("non-symbol line " + line_number + " (weird # directive?)");
                            return false;
                    }
                }

                if (sb.ToString().ToUpperInvariant() != "SYMBOL")
                {
                    Console.WriteLine("non-symbol line " + line_number + " ('#" + sb.ToString() + "' instead of #SYMBOL )");
                    return false;
                }
            }

            {
                bool bFound = find_token(input_line, ref index);

                if (!bFound)
                {
                    Console.WriteLine("broken symbol line " + line_number + ": can't find anything past #SYMBOL");
                    return false;
                }
            }

            string symbol_name = read_symbol_name(input_line, ref index);

            if (symbol_name == null)
            {
                return false;
            }

            {
                bool bFound = find_token(input_line, ref index);

                if (!bFound)
                {
                    Console.WriteLine("broken symbol line " + line_number + ": can't find anything past name '" + symbol_name + "'");
                    return false;
                }
            }

            string symbol_value = ReadNumber(input_line, ref index);
            if (symbol_value == null)
            {
                return false;
            }

            Label l = new Label();
            l.is_name_final = true;
            l.name = symbol_name;
            if (symbol_value.ToUpperInvariant().StartsWith("0X"))
            {
                try
                {
                    l.address = Convert.ToUInt32(symbol_value, 16);
                }
                catch(Exception e)
                {
                    Console.WriteLine("unable to read hex value '" + symbol_value + "' : " + e.Message);
                    return false;
                }
            }
            else
            {
                try
                {
                    l.address = uint.Parse(symbol_value, CultureInfo.InvariantCulture);
                }
                catch(Exception e)
                {
                    Console.WriteLine("unable to read decimal value '" + symbol_value + "' : " + e.Message);
                    return false;
                }
            }
            labels.Add(l.address, l);

            return true;
        }

        static void rename_labels(List<SH4Word> words)
        {
            if (!is_comments_enabled)
            {
                return;
            }

            for (int i = 0; i < words.Count; i++)
            {
                uint addr = index_to_address(i);
                if (labels.ContainsKey(addr) && !labels[addr].is_name_final)
                {
                    SH4Word word = words[i];
                    if (words[i].is_data)
                    {
                        labels[addr].name = "data_" + addr.ToString("X2");
                    }
                    else if (labels[addr].is_local)
                    {
                        labels[addr].name = "l_code_" + addr.ToString("X2");
                    }
                    else
                    {
                        labels[addr].name = "f_code_" + addr.ToString("X2");
                    }
                }
            }

            foreach (uint addr in labels.Keys)
            {
                if (!labels[addr].is_name_final)
                {
                    if (labels[addr].is_function)
                    {
                        labels[addr].name = "fn_" + labels[addr].name;
                    }
                    else if (labels[addr].is_code)
                    {
                        labels[addr].name = "code_" + labels[addr].name;
                    }
                }

                recursive_identify_ptr(words, addr);
            }
        }

        static string recursive_identify_ptr(List<SH4Word> words, uint addr)
        {
            if (labels.ContainsKey(addr))
            {
                if (labels[addr].is_name_final)
                {
                    return labels[addr].name;
                }

                int index = address_to_index(addr);

                if (addr >= starting_offset
                    && index < words.Count
                    && words[index].is_double_word_data
                    && index % 2 == 0)
                {
                    uint raw_value = (uint)(words[index].raw_value | (words[index + 1].raw_value << 16));

                    string ptr_to = recursive_identify_ptr(words, raw_value);
                    

                    if (ptr_to != null)
                    {
                        labels[addr].name = "ptr_" + addr.ToString("X2") + "_to_" + ptr_to;
                    }
                }

                return labels[addr].name;
            }

            return null;
        }

        static uint index_to_address(int index)
        {
            return starting_offset + (uint)(index * 2);
        }

        static void try_to_add_extern_label(uint addr)
        {
            if (!labels.ContainsKey(addr))
            {
                Label l = new Label();
                l.is_name_final = false;
                l.index = -1;
                l.name = "extern_" + addr.ToString("X2");
                l.address = addr;
                l.is_used = true;
                labels.Add(addr, l);
            }
            else
            {
                labels[addr].is_used = true;
            }
        }

        static void try_to_add_label_at_address(uint addr)
        {
            int index = address_to_index(addr);

            if (!labels.ContainsKey(addr))
            {
                Label l = new Label();
                l.index = index;
                l.name = "loc_" + addr.ToString("X2");
                l.address = addr;
                l.is_name_final = false;
                l.is_used = true;
                l.is_local = !probable_addresses.ContainsKey(addr);
                labels.Add(addr, l);
            }
            else
            {
                labels[addr].is_used = true;
                labels[addr].index = index;
            }
        }

        static void try_to_add_label_at_index(int index)
        {
            uint addr = index_to_address(index);

            if (!labels.ContainsKey(addr))
            {
                Label l = new Label();
                l.index = index;
                l.name = "loc_" + addr.ToString("X2");
                l.address = addr;
                l.is_name_final = false;
                l.is_used = true;
                l.is_local = !probable_addresses.ContainsKey(index_to_address(index));
                labels.Add(addr, l);
            }
            else
            {
                labels[addr].is_used = true;
                labels[addr].index = index;
            }
        }

        static void follow_code_flow(List<SH4Word> words, int start_index)
        {
            if (start_index < 0)
            {
                // fixme this SUCKS
                return;
            }

            if (start_index >= words.Count)
            {
                return;
            }

            if (words[start_index].is_already_jumped)
            {
                return;
            }

            words[start_index].is_already_jumped = true;

            try_to_add_label_at_index(start_index);

            ExecutionState current_execution_state = new ExecutionState();

            bool is_previous_delay = false;
            bool is_jmp_actually_a_call = false;
            for (int i = start_index; i < words.Count; i++)
            {
                SH4Word insn = words[i];

                if (insn.template != null)
                {
                    insn.is_data = false;
                    insn.is_double_word_data = false;

                    if (is_previous_delay)
                    {
                        return;
                    }

                    if (insn.template.is_jump)
                    {
                        if (insn.template.tokens[0] == "JSR "
                            && insn.template.args.ContainsKey("n"))
                        {
                            int reg_index = insn.args[insn.template.args["n"].index];

                            if (current_execution_state.reg_set[reg_index])
                            {
                                uint call_target = current_execution_state.reg_value[reg_index];

                                if (labels.ContainsKey(call_target))
                                {
                                    labels[call_target].is_local = false;
                                    labels[call_target].is_function = true;

                                    labels[call_target].add_execution_state(current_execution_state);
                                }
                            }
                        } else if (insn.template.tokens[0] == "JMP "
                            && insn.template.args.ContainsKey("n"))
                        {
                            int reg_index = insn.args[insn.template.args["n"].index];

                            if (current_execution_state.reg_set[reg_index])
                            {
                                uint call_target = current_execution_state.reg_value[reg_index];

                                if (labels.ContainsKey(call_target))
                                {
                                    labels[call_target].is_local = false;
                                    labels[call_target].is_code = !is_jmp_actually_a_call;
                                    labels[call_target].is_function |= is_jmp_actually_a_call;

                                    labels[call_target].add_execution_state(current_execution_state);
                                }
                            }
                        }

                        follow_code_flow(words, find_jump_target_index(insn, i));

                        if (!insn.template.is_call)
                        {
                            if (insn.template.is_delayed)
                            {
                                is_previous_delay = true;
                            }
                            else
                            {
                                return;
                            }
                        }
                        else
                        {
                            // need to reset this stuff
                            is_jmp_actually_a_call = false;
                        }
                    }

                    if (insn.template.is_pc_disp_load)
                    {
                        int disp_index = find_pc_disp_load(words, insn, i);

                        if (disp_index < words.Count)
                        {
                            if (insn.template.tokens[0].StartsWith("MOV.W"))
                            {
                                int index = insn.args[1];
                                current_execution_state.reg_set[index] = true;
                                current_execution_state.reg_accessed[index] = true;
                                current_execution_state.reg_value[index] = words[disp_index].raw_value;
                                words[disp_index].is_double_word_data = false;

                                add_comment_to_word(insn, "r" + index + " set to 0x" + current_execution_state.reg_value[index].ToString("X2"));
                            }
                            else if (insn.template.tokens[0].StartsWith("MOV.L"))
                            {
                                int index = insn.args[1];
                                current_execution_state.reg_set[index] = true;
                                current_execution_state.reg_accessed[index] = true;
                                current_execution_state.reg_value[index] = (uint)(words[disp_index].raw_value | (words[disp_index + 1].raw_value << 16));
                                add_comment_to_word(insn, "r" + index + " set to 0x" + current_execution_state.reg_value[index].ToString("X2"));
                            }
                            else if (insn.template.tokens[0].StartsWith("MOVA"))
                            {
                                set_reg_to_value(current_execution_state, insn,
                                        0, index_to_address(disp_index));
                            }// TODO MOVA??
                        }
                    }

                    if (insn.template.is_return)
                    {
                        if (insn.template.is_delayed)
                        {
                            is_previous_delay = true;
                        }
                        else
                        {
                            return;
                        }
                    }

                    if (!insn.template.is_no_mutate && insn.template.args != null)
                    {
                        if (insn.template.args.ContainsKey("n"))
                        {
                            int index_n = insn.args[insn.template.args["n"].index];

                            if (index_n < current_execution_state.reg_set.Length)
                            {
                                current_execution_state.reg_accessed[index_n] = true;

                                if (insn.template.args.ContainsKey("i"))
                                {
                                    sbyte imm = unchecked((sbyte)insn.args[insn.template.args["i"].index]);

                                    if (current_execution_state.reg_set[index_n])
                                    {
                                        switch (insn.template.tokens[0])
                                        {
                                            case "ADD ":
                                                set_reg_to_value(current_execution_state, insn,
                                                    index_n, (uint)(current_execution_state.reg_value[index_n] + imm));
                                                break;
                                            case "MOV ":
                                                set_reg_to_value(current_execution_state, insn,
                                                    index_n, (uint)(imm));
                                                break;
                                            default:
                                                add_comment_to_word(insn, "r" + index_n + " trashed??");

                                                current_execution_state.reg_set[index_n] = false;
                                                break;
                                        }
                                    }
                                    else
                                    {
                                        switch (insn.template.tokens[0])
                                        {
                                            case "MOV ":
                                                set_reg_to_value(current_execution_state, insn,
                                                    index_n, (uint)(imm));
                                                break;
                                            default:
                                                break;
                                        }
                                    }
                                }
                                else if (insn.template.args.ContainsKey("m"))
                                {
                                    if (insn.template.args.ContainsKey("d"))
                                    {
                                        current_execution_state.loaded_values.Add((uint)insn.args[insn.template.args["d"].index]);
                                    }

                                    int index_m = insn.args[insn.template.args["m"].index];

                                    if (index_m < current_execution_state.reg_set.Length
                                        && current_execution_state.reg_set[index_m])
                                    {
                                        current_execution_state.reg_accessed[index_m] = true;

                                        switch (insn.template.tokens[0])
                                        {
                                            case "ADD ":
                                                if (current_execution_state.reg_set[index_n])
                                                {
                                                    set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] + current_execution_state.reg_value[index_m]);
                                                }
                                                break;
                                            case "SUB ":
                                                if (current_execution_state.reg_set[index_n])
                                                {
                                                    set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] - current_execution_state.reg_value[index_m]);
                                                }
                                                break;
                                            case "MOV ":
                                                set_reg_to_value(current_execution_state, insn,
                                                    index_n, current_execution_state.reg_value[index_m]);
                                                break;
                                            case "EXTU.B ":
                                                set_reg_to_value(current_execution_state, insn,
                                                    index_n, current_execution_state.reg_value[index_m] & 0x000000FF);
                                                break;
                                            case "EXTU.W ":
                                                set_reg_to_value(current_execution_state, insn,
                                                    index_n, current_execution_state.reg_value[index_m] & 0x0000FFFF);
                                                break;
                                            default:
                                                if (current_execution_state.reg_set[index_n])
                                                {
                                                    add_comment_to_word(insn, "r" + index_n + " ??");

                                                    current_execution_state.reg_set[index_n] = false;
                                                }
                                                // dont emit text on other case
                                                break;
                                        }
                                    }
                                    else if (current_execution_state.reg_set[index_n])
                                    {
                                        add_comment_to_word(insn, "r" + index_n + " ??? bc r" + index_m + " is ???");

                                        current_execution_state.reg_set[index_n] = false;
                                    }
                                } // done 2 regs cases
                                else
                                {
                                    if (current_execution_state.reg_set[index_n])
                                    {
                                        current_execution_state.reg_accessed[index_n] = true;

                                        switch (insn.template.tokens[0])
                                        {
                                            case "SHLL ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] << 1);
                                                break;
                                            case "SHLL2 ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] << 2);
                                                break;
                                            case "SHLL8 ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] << 8);
                                                break;
                                            case "SHLL16 ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] << 16);
                                                break;
                                            case "SHRR ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] >> 1);
                                                break;
                                            case "SHRR2 ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] >> 2);
                                                break;
                                            case "SHRR8 ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] >> 8);
                                                break;
                                            case "SHRR16 ":
                                                set_reg_to_value(current_execution_state, insn,
                                                        index_n, current_execution_state.reg_value[index_n] >> 16);
                                                break;
                                            default:
                                                break;
                                        }
                                    }
                                }
                            }
                        }
                        else if (insn.template.args.ContainsKey("m"))
                        {
                            int index_m = insn.args[insn.template.args["m"].index];

                            if (insn.template.tokens[0] == "LDS.L "
                                && insn.template.tokens.Count == 4
                                && insn.template.tokens[3] == "+,PR "
                                && index_m == 15)
                            {
                                is_jmp_actually_a_call = true;
                            }
                        }
                    }
                }
            } // for
        } //follow code flow

        static void set_reg_to_value(ExecutionState current_execution_state, SH4Word insn, int index, uint new_value)
        {
            current_execution_state.loaded_values.Add(new_value);
            current_execution_state.reg_value[index] = new_value;
            bool bInitialized = current_execution_state.reg_accessed[index];
            current_execution_state.reg_set[index] = true;
            current_execution_state.reg_accessed[index] = true;
            add_comment_to_word(insn,
                "r" + index
                + (bInitialized ? " set" : " init")
                + " to 0x" + current_execution_state.reg_value[index].ToString("X2"));
        }
        
        static bool check_code_flow(List<SH4Word> words, int start_index)
        {
            bool is_previous_delay = false;


            int word_count = words.Count;
            if (start_index > word_count)
            {
                return false;
            }
            
            if (start_index < 0)
            {
                // fixme this SUCKS
                return true;
            }

            if (words[start_index].is_already_jumped)
            {
                return true;
            }

            if (words[start_index].code_check_status == eCodeCheckStatus.Failed)
            {
                return false;
            }

            if (words[start_index].code_check_status == eCodeCheckStatus.Success)
            {
                return true;
            }

            words[start_index].is_already_jumped = true;

            for (int i = start_index; i < word_count; i++)
            {
                SH4Word insn = words[i];
                
                if (insn.code_check_status == eCodeCheckStatus.Failed)
                {
                    words[start_index].code_check_status = eCodeCheckStatus.Failed;
                    return false;
                }

                if (insn.code_check_status == eCodeCheckStatus.Success)
                {
                    words[start_index].code_check_status = eCodeCheckStatus.Success;
                    return true;
                }


                if (insn.template != null)
                {
                    if (!is_code_kernel_mode && insn.template.is_priv)
                    {
                        words[start_index].code_check_status = eCodeCheckStatus.Failed;
                        return false;
                    }

                    if (is_previous_delay)
                    {
                        words[start_index].is_already_jumped = false;
                        words[start_index].code_check_status = eCodeCheckStatus.Success;
                        return true;
                    }

                    if (insn.template.is_jump)
                    {
                        check_code_flow(words, find_jump_target_index(insn, i));

                        if (!insn.template.is_call)
                        {
                            if (insn.template.is_delayed)
                            {
                                is_previous_delay = true;
                            }
                            else
                            {
                                words[start_index].is_already_jumped = false;
                                words[start_index].code_check_status = eCodeCheckStatus.Success;
                                return true;
                            }
                        }
                    }

                    if (insn.template.is_return)
                    {
                        if (insn.template.is_delayed)
                        {
                            is_previous_delay = true;
                        }
                        else
                        {
                            words[start_index].is_already_jumped = false;
                            return true;
                        }
                    }
                }
                else
                {
                    words[start_index].code_check_status = eCodeCheckStatus.Failed;
                    insn.code_check_status = eCodeCheckStatus.Failed;
                    return false;
                }
            } // for

            // reached eof
            words[start_index].code_check_status = eCodeCheckStatus.Failed;
            return false;
        }

        static uint find_pc_disp_load_addr(SH4Word word, int cur_index)
        {
            int disp = word.args[word.template.args["d"].index];

            uint addr = (uint)(index_to_address(cur_index) + disp);

            if (word.template.displace_size == 4)
            {
                addr = addr & 0xFFFF_FFFC;
            }
            return addr;
        }

        static int find_pc_disp_load(List<SH4Word> words, SH4Word word, int cur_index)
        {
            if (word.template.args.ContainsKey("d"))
            {
                uint addr = find_pc_disp_load_addr(word, cur_index);

                int index = address_to_index(addr);


                try_to_add_label_at_index(index);

                if (index < words.Count)
                {
                    if (word.template.displace_size != 4)
                    {
                        words[index].is_double_word_data = false;

                        if (index % 2 == 0)
                        {
                            words[index + 1].is_double_word_data = false;
                        }

                        if (word.template.displace_size == 1)
                        {
                            words[index].is_byte_data = true;
                        }
                    }
                    else
                    {
                        words[index].is_double_word_data = true;
                        words[index + 1].is_double_word_data = true;
                    }
                }

                return index;
            }

            throw new NotImplementedException();
        }

        static int find_jump_target_index(SH4Word word, int cur_index)
        {
            if (word.template.args.ContainsKey("d"))
            {
                int disp = word.args[word.template.args["d"].index];

                int index = (disp / 2) + cur_index;


                try_to_add_label_at_index(index);

                return index;
            }

            if (word.template.is_register_indirect_jump)
            {
                // todo get some rudimentary stuff here
                return -1;
            }

            throw new NotImplementedException();

            //return -1;
        }

        static void find_probable_addresses(List<SH4Word> words)
        {
            probable_addresses = new Dictionary<uint, List<uint>>();

            for (int i = 0; i < words.Count - 1; i += 2)
            {
                uint ptr = (uint)(words[i].raw_value | (words[i + 1].raw_value << 16));
                uint src = index_to_address(i);
                
                if (ptr > starting_offset && ptr < ending_offset)
                {
                    // fixme check alignment (??)
                    if (!probable_addresses.ContainsKey(ptr))
                    {
                        probable_addresses.Add(ptr, new List<uint>());
                    }

                    try_to_add_label_at_address(ptr);
                    try_to_add_label_at_index(address_to_index(src));
                    probable_addresses[ptr].Add(src);
                }


                // FIXME THIS SUCKS BAD THAT ITS HARDCODED
                // original 1st_Read is 8C1B9D99
                if (ending_offset < 0x8c000000 || starting_offset >= 0x8e000000)
                {
                    if (ptr > 0x8c000000 && ptr < 0x8e000000)
                    {
                        try_to_add_extern_label(ptr);

                        if (!probable_addresses.ContainsKey(ptr))
                        {
                            probable_addresses.Add(ptr, new List<uint>());
                        }

                        probable_addresses[ptr].Add(src);
                    }
                }
            }
        }

        static int address_to_index(uint addr)
        {
            return (int)((addr - starting_offset) / 2);
        }

        static StringBuilder build_text_output(List<SH4Word> words, StringBuilder sb)
        {
            if (sb == null)
            {
                sb = new StringBuilder();
            }

            foreach (Label l in labels.Values)
            {
                if (l.index < 0 && l.is_used)
                {
                    sb.Append("#symbol ");
                    sb.Append(l.name);
                    sb.Append(" 0x");
                    sb.Append(l.address.ToString("X8"));
                    sb.Append("\n");
                }
            }

            if (is_comments_enabled)
            {
                sb.Append("\n\n\n;");
                sb.Append('=', 70);
                sb.Append("\n\n");
            }

            for (int i = 0; i < words.Count; i++)
            {
                SH4Word word = words[i];

                if (i > 3
                    && words[i - 2].template != null
                    && words[i-2].template.is_return)
                {
                    sb.Append(';');
                    sb.Append('-', 79);
                    sb.Append("\n");
                }
                else if (i > 0 && words[i - 1].is_data && !word.is_data)
                {
                    sb.Append("\n");
                }

                if (word.repeat_count > 0)
                {
                    sb.Append("\n#repeat ");
                    sb.Append(word.repeat_count);
                    sb.Append("\n");
                }

                if (word.is_data)
                {
                    if (index_to_address(i) % 4 != 0)
                    {
                        word.is_double_word_data = false;
                    }
                }

                if (word.is_data)
                {
                    if (!word.is_byte_data)
                    {
                        // adjust for labels pointing inside of it
                        uint addr = index_to_address(i) + 1;
                        uint max_test_address = addr + 2;
                        if (word.is_double_word_data)
                        {
                            max_test_address = addr + 3;
                        }

                        while (addr < max_test_address) // has break;
                        {
                            if (labels.ContainsKey(addr))
                            {
                                if (addr % 2 == 1)
                                {
                                    word.is_byte_data = true;
                                    word.is_double_word_data = false;
                                    break; // while(addr < max_test_address)
                                }
                                else
                                {
                                    word.is_double_word_data = false;
                                }
                            }
                            addr++;
                        }
                    }
                }

                if (labels.ContainsKey(index_to_address(i)))
                {
                    if (i > 0)
                    {
                        // if previous line was not a doubleword with a label
                        // and the previous line's data is a label
                        if (word.is_double_word_data)
                        {
                            if (!words[i - 1].is_align4)
                            {
                                sb.Append("\n#align4\n");
                            }
                            else
                            {
                                if (i > 1 && words[i - 2].is_double_word_data)
                                {
                                    uint raw_previous_value = (uint)(words[i - 2].raw_value | (words[i - 1].raw_value << 16));

                                    if (!labels.ContainsKey(raw_previous_value))
                                    {
                                        sb.Append("\n#align4\n");
                                    }
                                    else
                                    {
                                        sb.Append('\n');
                                    }
                                }
                                else
                                {
                                    sb.Append('\n');
                                }
                            }
                            word.is_align4 = true;
                            words[i+1].is_align4 = true;
                        }
                        else
                        {
                            sb.Append("\n");
                        }
                    }
                    else if(word.is_double_word_data)
                    {
                        word.is_align4 = true;
                    }
                    sb.Append(labels[index_to_address(i)].name);
                    sb.Append(":\n");
                }

                // fixme divide this into functions
                if (word.is_data)
                {
                    if (word.is_double_word_data)
                    {
                        uint raw_value = (uint)(words[i].raw_value | (words[i + 1].raw_value << 16));
                        if (labels.ContainsKey(raw_value))
                        {
                            sb.Append("#data ");

                            if (labels[raw_value].index < 0
                                || raw_value > ending_offset)
                            {
                                if (labels[raw_value].module.Length > 0)
                                {
                                    sb.Append(labels[raw_value].module);
                                    sb.Append('.');
                                }
                            }
                            sb.Append(labels[raw_value].name);
                        }
                        else
                        {
                            sb.Append("#data 0x");

                            sb.Append(raw_value.ToString("X8"));
                        }


                        if (word.comment != null)
                        {
                            sb.Append(" ; ");
                            sb.Append(word.comment);
                        }

                        if (words[i+1].comment != null)
                        {
                            sb.Append(" ; ");
                            sb.Append(words[i + 1].comment);
                        }

                        if (is_comments_enabled)
                        {
                            sb.Append("\t\t; addr: ");
                            sb.Append((i * 2 + starting_offset).ToString("X7"));
                        }
                        sb.Append("\n");

                        i++;
                    }
                    else
                    {
                        if (word.is_byte_data)
                        {
                            sb.Append("#data 0x");

                            sb.Append((word.raw_value & 0x00FF).ToString("X2"));

                            if (labels.ContainsKey(index_to_address(i) + 1))
                            {
                                sb.Append('\n');
                                sb.Append(labels[index_to_address(i) + 1].name);
                                sb.Append(":\n#data");
                            }

                            sb.Append(" 0x");
                            sb.Append(((word.raw_value >> 8) & 0x00FF).ToString("X2"));
                        }
                        else
                        {
                            sb.Append("#data 0x");

                            sb.Append(word.raw_value.ToString("X4"));
                        }

                        if (word.comment != null)
                        {
                            sb.Append(" ; ");
                            sb.Append(word.comment);
                        }
                        if (is_comments_enabled)
                        {
                            sb.Append("\t\t; addr: ");
                            sb.Append((i * 2 + starting_offset).ToString("X7"));
                        }
                        sb.Append("\n");
                    }
                }
                else
                {
                    foreach (string token in word.template.tokens)
                    {
                        if (token.Length == 1
                            && word.template.args.ContainsKey(token))
                        {
                            int arg_index = word.template.args[token].index;
                            if (word.template.args[token].is_register)
                            {
                                sb.Append(word.args[arg_index]);
                            }
                            else
                            {
                                if (word.template.is_jump)
                                {
                                    int target = find_jump_target_index(word, i);
                                    if (target != -1
                                        && labels.ContainsKey(index_to_address(target)))
                                    {
                                        if (labels[index_to_address(target)].index < 0
                                            || target > words.Count)
                                        {
                                            if (labels[index_to_address(target)].module.Length > 0)
                                            {
                                                sb.Append(labels[index_to_address(target)].module);
                                                sb.Append('.');
                                            }
                                        }

                                        sb.Append(labels[index_to_address(target)].name);
                                    }
                                    else
                                    {
                                        sb.Append("0x");
                                        sb.Append(word.args[arg_index].ToString("X2"));
                                    }
                                }
                                else if (word.template.is_pc_disp_load && token == "d")
                                {
                                    uint pc_disp_addr = index_to_address(find_pc_disp_load(words, word, i));

                                    if (labels.ContainsKey(pc_disp_addr))
                                    {
                                        if (labels[pc_disp_addr].index < 0
                                            || pc_disp_addr > words.Count)
                                        {
                                            if (labels[pc_disp_addr].module.Length > 0)
                                            {
                                                sb.Append(labels[pc_disp_addr].module);
                                                sb.Append('.');
                                            }
                                        }

                                        sb.Append(labels[pc_disp_addr].name);
                                    }
                                    else
                                    {
                                        sb.Append("0x");
                                        sb.Append(word.args[arg_index].ToString("X2"));
                                    }
                                }
                                else
                                {
                                    // fixme poor use of flow control, duplicate code
                                    sb.Append("0x");
                                    sb.Append(word.args[arg_index].ToString("X2"));
                                }
                            }
                        }
                        else
                        {
                            sb.Append(token.ToLowerInvariant());
                        }
                    }

                    if (word.comment != null)
                    {
                        sb.Append(" ; ");
                        sb.Append(word.comment);
                    }

                    if(is_comments_enabled)
                    {
                        sb.Append("\t\t; addr: ");
                        sb.Append((i * 2 + starting_offset).ToString("X7"));
                    }
                    sb.Append("\n");
                }


                if (word.repeat_count > 0)
                {
                    i += word.repeat_count - 1;
                }
            }

            return sb;
        }

        static void add_comment_to_word(SH4Word word, string comment)
        {
            if (is_comments_enabled)
            {
                if (word.comment == null)
                {
                    word.comment = comment;
                    return;
                }

                word.comment += ", " + comment;
            }
        }

        static SH4Word read_insn(ushort insn)
        {
            SH4Word output = new SH4Word();
            output.raw_value = insn;

            InstructionTemplate cur_template = null;
            foreach (InstructionTemplate t in templates)
            {
                if (t.check == (insn & t.and_mask))
                {
                    cur_template = t;
                    break;
                }
            }

            output.template = cur_template;

            if (cur_template == null)
            {
                output.is_data = true;
            }
            else
            {
                foreach (string token in cur_template.tokens)
                {
                    if (token.Length == 1
                        && cur_template.args.ContainsKey(token))
                    {
                        int arg = (insn & cur_template.args[token].mask) >> cur_template.args[token].shift;
                        if (output.args == null)
                        {
                            output.args = new List<int>();
                        }

                        if (token == "d")
                        {
                            if (cur_template.is_signed_displacement)
                            {
                                if (arg >= (1 << (cur_template.args[token].size - 1)))
                                {
                                    int sign_extend_bits = ~((1 << cur_template.args[token].size) - 1);

                                    arg |= sign_extend_bits;
                                }
                            }

                            if (cur_template.displace_size == 4)
                            {
                                arg *= 4;

                                if (!cur_template.is_no_add_disp)
                                {
                                    arg += 4;
                                }
                            }
                            else
                            {
                                arg *= cur_template.displace_size;

                                if (!cur_template.is_no_add_disp)
                                {
                                    arg += 4;
                                }
                            }
                        }

                        int arg_index = cur_template.args[token].index;
                        output.args.Add(arg);
                    }
                }
            }

            // FIXME THIS SUX
            output.is_data = true;

            return output;
        }

        static void load_instruction_templates(string filename)
        {
            string[] lines = File.ReadAllLines(filename);

            templates = new List<InstructionTemplate>(263);
            for (int line_number = 0; line_number < lines.Length; line_number++)
            {
                InstructionTemplate t = load_instruction_template_line(lines[line_number], line_number);

                if (t != null)
                {
                    templates.Add(t);
                }
            }
        }

        static InstructionTemplate load_instruction_template_line(string line, int line_number)
        {
            if (line.Length < 0)
            {
                return null;
            }

            char[] line_chars = line.ToCharArray();

            int potential_mask_length = 0;
            int potential_mask_start = -1;

            Dictionary<char, int> start_ranges = new Dictionary<char, int>();
            Dictionary<char, int> end_ranges = new Dictionary<char, int>();
            {
                bool is_special = false;
                for (int i = 0; i < line_chars.Length; i++)
                {
                    char c = line_chars[i];
                    switch (c)
                    {
                        case '#':
                            is_special = true;
                            potential_mask_length = 0;
                            potential_mask_start = -1;
                            break;
                        case 'd':
                        case 'n':
                        case 'm':
                        case 'i':
                            if (is_special)
                            {
                                break;
                            }

                            if (!start_ranges.ContainsKey(c))
                            {
                                start_ranges.Add(c, i);
                                end_ranges.Add(c, i);
                            }
                            else
                            {
                                if (i - end_ranges[c] > 1 || start_ranges[c] < potential_mask_start)
                                {
                                    start_ranges[c] = i;
                                    end_ranges[c] = i;
                                }
                                else
                                {
                                    end_ranges[c]++;
                                }
                            }

                            if (potential_mask_length == 0)
                            {
                                potential_mask_start = i;
                            }
                            potential_mask_length++;
                            break;
                        case '0':
                        case '1':
                            if (potential_mask_length == 0)
                            {
                                potential_mask_start = i;
                            }
                            potential_mask_length++;

                            if (potential_mask_length == 16)
                            {
                                i = line_chars.Length;
                            }
                            break;
                        case ' ':
                        case '\t':
                        case '\n':
                        case '\r':
                            is_special = false;
                            potential_mask_length = 0;
                            potential_mask_start = -1;
                            break;
                        default:
                            potential_mask_length = 0;
                            potential_mask_start = -1;
                            break;
                    }
                }
            }

            if (potential_mask_length < 16)
            {
                Console.WriteLine("mask error");
                Console.WriteLine(line);
                Console.WriteLine(potential_mask_start);
                Console.WriteLine(potential_mask_length);
                return null;
            }

            InstructionTemplate output = new InstructionTemplate();

            foreach (char c in start_ranges.Keys)
            {
                if (start_ranges[c] > potential_mask_start)
                {
                    if (output.args == null)
                    {
                        output.args = new Dictionary<string, ArgumentTemplate>();
                    }

                    int relative_start = 16 - (start_ranges[c] - potential_mask_start);
                    int relative_end = 15 - (end_ranges[c] - potential_mask_start);
                    ushort start_mask = (ushort)((1 << relative_start) - 1);
                    ushort end_mask = (ushort)~((1 << relative_end) - 1);
                    ushort mask = (ushort)(start_mask & end_mask);
                    ArgumentTemplate arg = new ArgumentTemplate();
                    arg.mask = mask;
                    arg.shift = relative_end;
                    arg.index = output.args.Count;
                    arg.size = end_ranges[c] - start_ranges[c] + 1;
                    output.args.Add(c.ToString(), arg);
                }
            }

            ushort mask_bit = 0b1000_0000_0000_0000;
            for (int i = potential_mask_start; i < potential_mask_start + 16; i++)
            {
                char c = line_chars[i];
                
                switch (c)
                {
                    case '0':
                        output.and_mask |= (ushort)mask_bit;
                        break;
                    case '1':
                        output.check |= (ushort)mask_bit;
                        output.and_mask |= (ushort)mask_bit;
                        break;
                    case 'd':
                    case 'i':
                    case 'm':
                    case 'n':
                        
                        break;
                    default:
                        throw new Exception("error loading mask");
                }
                mask_bit = (ushort)(mask_bit >> 1);
            }

            output.tokens = new List<string>();

            StringBuilder cur_token = new StringBuilder();
            {
                bool is_special = false;
                for (int i = 0; i < potential_mask_start; i++)
                {
                    char c = line_chars[i];
                    switch (c)
                    {
                        case 'd':
                        case 'n':
                        case 'm':
                        case 'i':
                            if (!is_special)
                            {
                                // if start ranges does not contain,
                                // there SHOULD be an exception thrown because something bad/wrong has happened
                                if (start_ranges[c] > potential_mask_start)
                                {
                                    output.tokens.Add(cur_token.ToString());
                                    cur_token.Clear();
                                    output.tokens.Add(c.ToString());
                                }

                                if (i > 0)
                                {
                                    char prev = line_chars[i - 1];

                                    if (prev == 'R' || prev == 'V')
                                    {
                                        output.args[c.ToString()].is_register = true;
                                    }
                                }
                            }
                            else
                            {
                                cur_token.Append(c);
                            }
                            break;
                        case ' ':
                        case '\t':
                        case '\r':
                        case '\n':
                            if (cur_token.Length > 0)
                            {
                                if (is_special)
                                {
                                    switch (cur_token.ToString())
                                    {
                                        case "priv":
                                            output.is_priv = true;
                                            break;
                                        case "ret":
                                            output.is_return = true;
                                            break;
                                        case "call":
                                            output.is_call = true;
                                            output.is_jump = true;
                                            break;
                                        case "jmp":
                                            output.is_jump = true;
                                            break;
                                        case "rjmp":
                                            output.is_register_indirect_jump = true;
                                            break;
                                        case "delay":
                                            output.is_delayed = true;
                                            break;
                                        case "pcdisp":
                                            output.is_pc_disp_load = true;
                                            break;
                                        case "signed_disp":
                                            output.is_signed_displacement = true;
                                            break;
                                        case "no_add_disp":
                                            output.is_no_add_disp = true;
                                            break;
                                        case "no_mutate":
                                            output.is_no_mutate = true;
                                            break;
                                        default:
                                            throw new NotImplementedException("#" + cur_token.ToString());
                                    }
                                }
                                else
                                {
                                    cur_token.Append(' ');
                                    output.tokens.Add(cur_token.ToString());
                                }

                                is_special = false;
                                cur_token.Clear();
                            }
                            break;
                        case '#':
                            is_special = true;
                            break;
                        default:
                            cur_token.Append(c);
                            break;
                    }
                }
            }

            output.displace_size = 2;

            if (output.tokens[0].Contains(".B"))
            {
                output.displace_size = 1;
            }
            else if (output.tokens[0].Contains(".L")
                || output.tokens[0].StartsWith("MOVA"))
            {
                output.displace_size = 4;
            }

            return output;
        } // load instruction template

        static void save_stats(string stats_filename)
        {
            if (String.IsNullOrWhiteSpace(stats_filename))
            {
                return;
            }
            StringBuilder sb = write_stats();

            string temp_filename = Path.GetTempFileName();

            File.WriteAllText(temp_filename, sb.ToString());

            File.Delete(stats_filename + ".bak");

            if (File.Exists(stats_filename))
            {
                File.Copy(stats_filename, stats_filename + ".bak");
            }

            File.Delete(stats_filename);
            File.Copy(temp_filename, stats_filename);
        }

        static StringBuilder write_stats()
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("addr,");
            for (int i = 0; i < 16; i++)
            {
                sb.Append('r');
                sb.Append(i);
                sb.Append(',');
            }

            sb.Append("values");
            sb.Append('\n');

            foreach (KeyValuePair<uint, Label> pair in labels)
            {
                Label l = pair.Value;

                if (l.is_used && (l.is_code || l.is_function))
                {
                    sb.Append(l.address.ToString("X2"));
                    sb.Append(',');
                    if (l.reg_possible_values != null)
                    {
                        for (int reg_index = 0; reg_index < 16; reg_index++)
                        {
                            if (l.reg_possible_values.ContainsKey(reg_index))
                            {
                                HashSet<uint> reg_values = l.reg_possible_values[reg_index];

                                sb.Append('"');
                                if (reg_values.Count == 0)
                                {
                                    sb.Append("ACCESSED");
                                }
                                else
                                {
                                    foreach (uint value in reg_values)
                                    {
                                        sb.Append("0x");
                                        sb.Append(value.ToString("X2"));
                                        sb.Append(", ");
                                    }

                                    sb.Length -= 2;
                                }
                                sb.Append('"');
                            }
                            else
                            {
                                sb.Append("no");
                            }

                            sb.Append(',');
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            sb.Append("no,");
                        }
                    }

                    if (l.loaded_values_before != null && l.loaded_values_before.Count > 0)
                    {
                        sb.Append('"');
                        foreach (uint value in l.loaded_values_before)
                        {
                            sb.Append("0x");
                            sb.Append(value.ToString("X2"));
                            sb.Append(", ");
                        }
                        sb.Length -= 2;
                        sb.Append('"');
                    }
                    sb.Append('\n');
                }
            }

            return sb;
        }
    } // program
} // ns

