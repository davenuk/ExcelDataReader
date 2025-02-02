using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Xml;
using ExcelDataReader.Core.OpenXmlFormat.BinaryFormat;
using ExcelDataReader.Core.OpenXmlFormat.Records;
using ExcelDataReader.Core.OpenXmlFormat.XmlFormat;

namespace ExcelDataReader.Core.OpenXmlFormat
{
    internal partial class ZipWorker : IDisposable
    {
        private const string FileSharedStrings = "xl/sharedStrings.{0}";
        private const string FileStyles = "xl/styles.{0}";
        private const string FileWorkbook = "xl/workbook.{0}";
        private const string FileRels = "xl/_rels/workbook.{0}.rels";

        // comments - or notes
        private const string FileComments = "xl/{0}";
        private const string SheetRels = "xl/worksheets/_rels/{1}.{0}.rels";
        private const string NsRelationship = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string ElementRelationship = "Relationship";
        private const string ElementRelationships = "Relationships";
        private const string AttributeType = "Type";
        private const string AttributeTarget = "Target";
        
        private const string Format = "xml";
        private const string BinFormat = "bin";

        private static readonly XmlReaderSettings XmlSettings = new XmlReaderSettings 
        {
            IgnoreComments = true, 
            IgnoreWhitespace = true,
        };

        private readonly Dictionary<string, ZipArchiveEntry> _entries;
        private bool _disposed;
        private Stream _zipStream;
        private ZipArchive _zipFile;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZipWorker"/> class. 
        /// </summary>
        /// <param name="fileStream">The zip file stream.</param>
        public ZipWorker(Stream fileStream)
        {
            _zipStream = fileStream ?? throw new ArgumentNullException(nameof(fileStream));
            _zipFile = new ZipArchive(fileStream);
            _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _zipFile.Entries)
            {
                _entries.Add(entry.FullName.Replace('\\', '/'), entry);
            }
        }

        /// <summary>
        /// Gets the shared strings reader.
        /// </summary>
        public RecordReader GetSharedStringsReader()
        {
            var entry = FindEntry(string.Format(FileSharedStrings, Format));
            if (entry != null)
                return new XmlSharedStringsReader(XmlReader.Create(entry.Open(), XmlSettings));

            entry = FindEntry(string.Format(FileSharedStrings, BinFormat));
            if (entry != null)
                return new BiffSharedStringsReader(entry.Open());

            return null;
        }

        /// <summary>
        /// Gets the cmments reader.
        /// </summary>
        public RecordReader GetCommentsReader(SheetRecord sheet)
        {
            //find comments sheet from sheet.rels
            //get threaded comment reader first?
            var sheetFilename = Path.GetFileNameWithoutExtension(sheet.Path);
            var entry = FindEntry(string.Format(SheetRels, Format, sheetFilename));
            if (entry == null)
                return null;
            var reader = XmlReader.Create(entry.Open(), XmlSettings);
            if (!reader.IsStartElement(ElementRelationships, NsRelationship))
            {
                return null;
            }

            if (!XmlReaderHelper.ReadFirstContent(reader))
            {
                return null;
            }

            var commentFileName = "";
            while (!reader.EOF)
            {
                if (reader.IsStartElement(ElementRelationship, NsRelationship))
                {
                    string relType = reader.GetAttribute(AttributeType);
                    
                    if (relType.EndsWith("comments"))
                    {
                        var target = reader.GetAttribute(AttributeTarget);
                        commentFileName = string.Format(FileComments,Path.GetFileName(target));
                        break;
                    }//todo - hardcodednono

                    reader.Skip();
                }
                else if (!XmlReaderHelper.SkipContent(reader))
                {
                    break;
                }
            }

            entry = FindEntry(commentFileName);
            if (entry != null)
                return new XmlCommentsReader(XmlReader.Create(entry.Open(), XmlSettings));

            //entry = FindEntry(string.Format(FileComments, BinFormat));
            //if (entry != null)
            //    return new BiffCommentsReader(entry.Open());

            return null;
        }

        /// <summary>
        /// Gets the styles reader.
        /// </summary>
        public RecordReader GetStylesReader()
        {
            var entry = FindEntry(string.Format(FileStyles, Format));
            if (entry != null)
                return new XmlStylesReader(XmlReader.Create(entry.Open(), XmlSettings));

            entry = FindEntry(string.Format(FileStyles, BinFormat));
            if (entry != null)
                return new BiffStylesReader(entry.Open());

            return null;
        }

        /// <summary>
        /// Gets the workbook reader.
        /// </summary>
        public RecordReader GetWorkbookReader()
        {
            var entry = FindEntry(string.Format(FileWorkbook, Format));
            if (entry != null)
                return new XmlWorkbookReader(XmlReader.Create(entry.Open(), XmlSettings));

            entry = FindEntry(string.Format(FileWorkbook, BinFormat));
            if (entry != null)
                return new BiffWorkbookReader(entry.Open());

            throw new Exceptions.HeaderException(Errors.ErrorZipNoOpenXml);
        }

        public RecordReader GetWorksheetReader(string sheetPath)
        {
            // its possible sheetPath starts with /xl. in this case trim the /
            // see the test "Issue_11522_OpenXml"
            if (sheetPath.StartsWith("/xl/", StringComparison.OrdinalIgnoreCase))
                sheetPath = sheetPath.Substring(1);
            else
                sheetPath = "xl/" + sheetPath;

            var zipEntry = FindEntry(sheetPath);
            if (zipEntry != null)
            {
                return Path.GetExtension(sheetPath) switch
                {
                    ".xml" => new XmlWorksheetReader(XmlReader.Create(zipEntry.Open(), XmlSettings)),
                    ".bin" => new BiffWorksheetReader(zipEntry.Open()),
                    _ => null,
                };
            }

            return null;
        }

        /// <summary>
        /// Gets the workbook rels stream.
        /// </summary>
        /// <returns>The rels stream.</returns>
        public Stream GetWorkbookRelsStream()
        {
            var zipEntry = FindEntry(string.Format(FileRels, Format));
            if (zipEntry != null)
                return zipEntry.Open();

            zipEntry = FindEntry(string.Format(FileRels, BinFormat));
            if (zipEntry != null)
                return zipEntry.Open();

            return null;
        }

        private ZipArchiveEntry FindEntry(string name)
        {
            if (_entries.TryGetValue(name, out var entry))
                return entry;
            return null;
        }
    }

    internal partial class ZipWorker
    {
        ~ZipWorker()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_zipFile != null)
                    {
                        _zipFile.Dispose();
                        _zipFile = null;
                    }

                    if (_zipStream != null)
                    {
                        _zipStream.Dispose();
                        _zipStream = null;
                    }
                }

                _disposed = true;
            }
        }
    }
}