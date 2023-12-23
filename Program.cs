﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Buffers;
using System.Buffers.Binary;

class Program
{
    static bool isSSLRequest(byte[] buffer)
    {
        // SSLRequest (F) 
        // Int32(8)
        // Length of message contents in bytes, including self.

        // Int32(80877103)
        // The SSL request code. The value is chosen to contain 1234 in the most significant 16 bits, and 5679 in the least significant 16 bits. (To avoid confusion, this code must not be the same as any protocol version number.)
        
        return 
            buffer[3] == 0x08
            && buffer[4] == 0x04 && buffer[5] == 0xd2 && buffer[6] == 0x16 && buffer[7] == 0x2f;
    }


    static (bool isStartupMessage, Dictionary<string,string> keyValues) isStartupMessage(byte[] buffer)
    {
        // StartupMessage (F) 
        // Int32
        // Length of message contents in bytes, including self.

        // Int32(196608) Note: 2^17+2^16 
        // The protocol version number. The most significant 16 bits are the major version number (3 for the protocol described here). The least significant 16 bits are the minor version number (0 for the protocol described here).
        // The protocol version number is followed by one or more pairs of parameter name and value strings. A zero byte is required as a terminator after the last name/value pair. Parameters can appear in any order. user is required, others are optional. Each parameter is specified as:

        // String
        // The parameter name. Currently recognized names are:

        // user
        // The database user name to connect as. Required; there is no default.

        // database
        // The database to connect to. Defaults to the user name.

        // options
        // Command-line arguments for the backend. (This is deprecated in favor of setting individual run-time parameters.) Spaces within this string are considered to separate arguments, unless escaped with a backslash (\); write \\ to represent a literal backslash.

        // replication
        // Used to connect in streaming replication mode, where a small set of replication commands can be issued instead of SQL statements. Value can be true, false, or database, and the default is false. See Section 55.4 for details.

        // In addition to the above, other parameters may be listed. Parameter names beginning with _pq_. are reserved for use as protocol extensions, while others are treated as run-time parameters to be set at backend start time. Such settings will be applied during backend start (after parsing the command-line arguments if any) and will act as session defaults.

        // String
        // The parameter value.

        bool isStartupMessage = buffer.Length > 8 
        && buffer[4] == 0x00 && buffer[5] == 0x03 && buffer[6] == 00 && buffer[7] == 00;

        if (!isStartupMessage)
        {
            return (false, null);
        }
        else 
        {
            Dictionary<string,string> keyValues = new Dictionary<string,string>();
            int i = 8;
            while (i < buffer.Length)
            {
                string key = "";
                while (buffer[i] != 0x00)
                {
                    key += (char)buffer[i];
                    i++;
                }
                i++;
                string value = "";
                while (buffer[i] != 0x00)
                {
                    value += (char)buffer[i];
                    i++;
                }
                i++;
                if(key!="")
                    keyValues.Add(key, value);
                if(buffer[i+1] == 0x00)
                    break;
            }
            return (true, keyValues);
        }
    }

    static bool isExit(byte[] buffer)
    {
        // Terminate (F) 
        // Byte1('X')
        // Identifies the message as a termination.

        // Int32(4)
        // Length of message contents in bytes, including self.

        return buffer[0] == (byte)'X';
    }

    static (bool isQuery, string QueryText) isQueryMessage(byte[] buffer)
    {
        // Query (F) 
        // Byte1('Q')
        // Identifies the message as a simple query.

        // Int32
        // Length of message contents in bytes, including self.

        // String
        // The query string itself.

        bool isQuery = buffer.Length > 5 && buffer[0] == (byte)'Q';

        if (!isQuery)
        {
            return (false, string.Empty);
        }
        else 
        {
            string QueryText = System.Text.Encoding.ASCII.GetString(buffer, 5, buffer.Length-5);
            return (true, QueryText);
        }
    }

     // AuthenticationOk (B) 
    // Byte1('R')
    // Identifies the message as an authentication request.

    // Int32(8)
    // Length of message contents in bytes, including self.

    // Int32(0)
    // Specifies that the authentication was successful.
    static readonly byte[] AuthenticationOk = new byte[] { 
        (byte)'R', 
        0x00, 0x00, 0x00, 0x08, 
        0x00, 0x00, 0x00, 0x00 }; 

    // BackendKeyData (B) 
    // Byte1('K')
    // Identifies the message as cancellation key data. The frontend must save these values if it wishes to be able to issue CancelRequest messages later.

    // Int32(12)
    // Length of message contents in bytes, including self.

    // Int32
    // The process ID of this backend.

    // Int32
    // The secret key of this backend.
    static readonly byte[] BackEndKey = new byte[] { 
        (byte)'K', 
        0x00, 0x00, 0x00, 0x0c, 
        0x00, 0x00, 0x04, 0xd2, 
        0x00, 0x00, 0x16, 0x2e };

    // ReadyForQuery (B) 
    // Byte1('Z')
    // Identifies the message type. ReadyForQuery is sent whenever the backend is ready for a new query cycle.

    // Int32(5)
    // Length of message contents in bytes, including self.

    // Byte1
    // Current backend transaction status indicator. Possible values are 'I' if idle (not in a transaction block); 'T' if in a transaction block; or 'E' if in a failed transaction block (queries will be rejected until block is ended).
    static readonly byte[] readyForQuery = new byte[] { 
        (byte)'Z', 
        0x00, 0x00, 0x00, 0x05, 
        (byte)'I' };

    public static void WriteBigEndian(BinaryWriter writer, Int32 value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        writer.Write(bytes);
    }

    public static void WriteBigEndian(BinaryWriter writer, Int16 value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        writer.Write(bytes);
    }

    public record ColumnDescriptionPg(
    string ColumnName, //must be ASCII serialized and zero terminated
    Int32 TableId, 
    Int16 ColumnId,
    Int32 DataTypeId, 
    Int16 DataTypeSize, 
    Int32 DataTypeModifier,
    Int16 FormatCode); 

    static void Main()
    {
        // Specify the IP address and port to listen on
        IPAddress ipAddress = IPAddress.Parse("0.0.0.0");
        int port = 5432;

        // Create a TCP listener
        TcpListener listener = new TcpListener(ipAddress, port);

        try
        {
            // Start listening for incoming connections
            listener.Start();
            Console.WriteLine("Listening for connections on port {0}...", port);

            // Accept incoming connections
            using TcpClient client = listener.AcceptTcpClient();
            Console.WriteLine("Connection accepted from {0}.", client.Client.RemoteEndPoint);
            NetworkStream stream = client.GetStream();
            bool isRunning = true;
            while(isRunning)
            {
                // Receive data from the client
                byte[] buffer = new byte[1024];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if(bytesRead > 0)
                {
                    string receivedData = System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    Console.WriteLine("Received data from client: {0}", receivedData);

                    //Grand central dispatch from here

                    if(isSSLRequest(buffer))
                    {
                        Console.WriteLine("Received SSL Request");
                        // NoSSL Negotiation
                        stream.Write(new byte[] {(byte)'N'});
                        continue;
                    }

                    if(isExit(buffer))
                    {
                        Console.WriteLine("Received Exit Message");
                        isRunning = false;
                        break;
                    }

                    (bool isStartup, Dictionary<string,string> keyValues) = isStartupMessage(buffer);
                    if(isStartup)
                    {
                        Console.WriteLine("Received Startup Message");
                        foreach (KeyValuePair<string,string> kvp in keyValues)
                        {
                            Console.WriteLine("Key = {0}, Value = {1}", kvp.Key, kvp.Value);
                        }

                        stream.Write(AuthenticationOk);            
                        stream.Write(BackEndKey);
                        stream.Write(readyForQuery);

                        continue;
                    }

                    (bool isQuery, string QueryText) = isQueryMessage(buffer);
                    if(isQuery)
                    {
                        Console.WriteLine("Received Query Message : " + QueryText);

                        //https://www.postgresql.org/docs/current/protocol-message-formats.html
                        // To reply, we need to send the following messages in order, a rowdescription, followed by zero or more data row, followed by a CommandComplete, followed by a ReadyForQuery.
                        // RowDescription (B)
                        // DataRow (B)
                        // DataRow (B) 
                        // CommandComplete (B)
                        // ReadyForQuery (B)



                        // RowDescription (B) 
                        // Byte1('T')
                        // Identifies the message as a row description.

                        // Int32
                        // Length of message contents in bytes, including self.

                        // Int16
                        // Specifies the number of fields in a row (can be zero).

                        // Then, for each field, there is the following:

                        // String
                        // The field name.

                        // Int32
                        // If the field can be identified as a column of a specific table, the object ID of the table; otherwise zero.

                        // Int16
                        // If the field can be identified as a column of a specific table, the attribute number of the column; otherwise zero.

                        // Int32
                        // The object ID of the field's data type.

                        // Int16
                        // The data type size (see pg_type.typlen). Note that negative values denote variable-width types.

                        // Int32
                        // The type modifier (see pg_attribute.atttypmod). The meaning of the modifier is type-specific.

                        // Int16
                        // The format code being used for the field. Currently will be zero (text) or one (binary). In a RowDescription returned from the statement variant of Describe, the format code is not yet known and will always be zero.
                        using MemoryStream msRowDescription = new MemoryStream();
                        
                        ColumnDescriptionPg[] columns = new ColumnDescriptionPg[] {
                            new ColumnDescriptionPg("id", 0x00, 0x00, 23, 4, -1, 0),
                            new ColumnDescriptionPg("name", 0x00, 0x00, 25, -1, -1, 0)
                        };


                        msRowDescription.Write(new byte[] {(byte)'T'},0,1);

                        Span<byte> Header = new byte[6];
                        BinaryPrimitives.WriteInt32BigEndian(Header, 0x00); //Length we will have to correct later
                        BinaryPrimitives.WriteInt16BigEndian(Header.Slice(4), (Int16)columns.Length); //Number of fields                       

                        msRowDescription.Write(Header);
                        //Field 1
                        
                        for (int i = 0; i < columns.Length; i++)
                        {
                            Span<byte> FieldName = Encoding.ASCII.GetBytes(columns[i].ColumnName + '\0');
                            Span<byte> FieldData = new byte[18];
                            BinaryPrimitives.WriteInt32BigEndian(FieldData.Slice(0), columns[i].TableId); //Table ID
                            BinaryPrimitives.WriteInt16BigEndian(FieldData.Slice(4), columns[i].ColumnId); //Attribute Number (column number)
                            BinaryPrimitives.WriteInt32BigEndian(FieldData.Slice(6), columns[i].DataTypeId); //Data Type ID - int32 = 23
                            BinaryPrimitives.WriteInt16BigEndian(FieldData.Slice(10), columns[i].DataTypeSize); //Data Type Size
                            BinaryPrimitives.WriteInt32BigEndian(FieldData.Slice(12), columns[i].DataTypeModifier); //Type Modifier
                            BinaryPrimitives.WriteInt16BigEndian(FieldData.Slice(16), columns[i].FormatCode); //Format Code

                            msRowDescription.Write(FieldName);
                            msRowDescription.Write(FieldData);
                        }

                        byte[] rowDescription = msRowDescription.ToArray();
                        msRowDescription.Close();
                        rowDescription[4] = (byte)(rowDescription.Length-1);
                        Console.WriteLine("rowDescription length = " + rowDescription.Length.ToString());
                        for(int i = 0; i < rowDescription.Length; i++)
                        {
                            Console.Write("{0:X2} ", rowDescription[i]);
                        }
                        Console.WriteLine();

                        stream.Write(rowDescription);

                        List<(int, string)> rows = new List<(int, string)>();
                        rows.Add((4, "four"));
                        rows.Add((6, "six"));
                        int nRows = rows.Count;
                        int nfields = 2;

                        // DataRow (B) 
                        // Byte1('D')
                        // Identifies the message as a data row.

                        // Int32
                        // Length of message contents in bytes, including self.

                        // Int16
                        // The number of column values that follow (possibly zero).

                        // Next, the following pair of fields appear for each column:

                        // Int32
                        // The length of the column value, in bytes (this count does not include itself). Can be zero. As a special case, -1 indicates a NULL column value. No value bytes follow in the NULL case.

                        // Byten
                        // The value of the column, in the format indicated by the associated format code. n is the above length.
                        for(int i = 0; i < rows.Count; i++)
                        {
                            using MemoryStream msDrRow1 = new MemoryStream();

                            msDrRow1.Write(new byte[] {(byte)'D'},0,1);
                            
                            Span<byte> rowdataheader = new byte[6];
                            BinaryPrimitives.WriteInt32BigEndian(rowdataheader.Slice(0), 0x00); //Length of field
                            BinaryPrimitives.WriteInt16BigEndian(rowdataheader.Slice(4), (Int16)nfields); //Number of fields
                            msDrRow1.Write(rowdataheader);

                            //how we write int32 fields
                            byte[] int32AsStringBytes = Encoding.ASCII.GetBytes(rows[i].Item1.ToString()); //What the hell, I send the int32 as a string ?
                            Span<byte> field1 = new byte[4+int32AsStringBytes.Length];
                            BinaryPrimitives.WriteInt32BigEndian(field1.Slice(0), 1); //Length of field
                            int32AsStringBytes.CopyTo(field1.Slice(4));
                            msDrRow1.Write(field1);

                            //write string fields
                            byte[] stringData = Encoding.ASCII.GetBytes(rows[i].Item2);
                            Span<byte> field2 = new byte[4+stringData.Length];
                            BinaryPrimitives.WriteInt32BigEndian(field2.Slice(0), stringData.Length); //Length of field
                            stringData.CopyTo(field2.Slice(4));
                            msDrRow1.Write(field2);

                            byte[] drRow1 = msDrRow1.ToArray();

                            msDrRow1.Close();
                            drRow1[4] = (byte)(drRow1.Length-1);
                            stream.Write(drRow1);

                            Console.WriteLine("DataRow1 length = " + drRow1.Length.ToString());
                            for(int k = 0; k < drRow1.Length;k++)
                            {
                                Console.Write("{0:X2} ", drRow1[k]);
                            }
                            Console.WriteLine();
                        }

                        // CommandComplete (B) 
                        // Byte1('C')
                        // Identifies the message as a command-completed response.

                        // Int32
                        // Length of message contents in bytes, including self.

                        // String
                        // The command tag. This is usually a single word that identifies which SQL command was completed.

                        // For an INSERT command, the tag is INSERT oid rows, where rows is the number of rows inserted. oid used to be the object ID of the inserted row if rows was 1 and the target table had OIDs, but OIDs system columns are not supported anymore; therefore oid is always 0.

                        // For a DELETE command, the tag is DELETE rows where rows is the number of rows deleted.

                        // For an UPDATE command, the tag is UPDATE rows where rows is the number of rows updated.

                        // For a MERGE command, the tag is MERGE rows where rows is the number of rows inserted, updated, or deleted.

                        // For a SELECT or CREATE TABLE AS command, the tag is SELECT rows where rows is the number of rows retrieved.

                        // For a MOVE command, the tag is MOVE rows where rows is the number of rows the cursor's position has been changed by.

                        // For a FETCH command, the tag is FETCH rows where rows is the number of rows that have been retrieved from the cursor.

                        // For a COPY command, the tag is COPY rows where rows is the number of rows copied. (Note: the row count appears only in PostgreSQL 8.2 and later.)   

                        using MemoryStream msCC = new MemoryStream();

                        msCC.Write(new byte[] {(byte)'C'});
                        byte[] msg = Encoding.ASCII.GetBytes("SELECT " + nRows.ToString() + '\0'); //Data
                        Span<byte> lenspan = new byte[4];
                        BinaryPrimitives.WriteInt32BigEndian(lenspan.Slice(0), msg.Length+4);
                        msCC.Write(lenspan); //Length of field
                        msCC.Write(msg);

                        byte[] cc = msCC.ToArray();
                        msCC.Close();

                        Console.WriteLine("cc length = " + cc.Length.ToString());
                        for(int i = 0; i < cc.Length; i++)
                        {
                            Console.Write("{0:X2} ", cc[i]);
                        }
                        Console.WriteLine();

                        stream.Write(cc);

                        // ReadyForQuery (B)
                        stream.Write(readyForQuery);
                        Console.WriteLine("return ready for query");
                        continue; 
                    }

                    
                    if(bytesRead > 0)
                    {
                        for(int i = 0; i < bytesRead; i++)
                        {
                            Console.Write("{0:X2} ", buffer[i]);
                        }
                    }
                }

            }
        }  
        catch (Exception ex)
        {
            Console.WriteLine("An error occurred: {0}", ex.Message);
        }
        finally
        {

            // Stop listening for connections
            listener.Stop();
        }
    }
}