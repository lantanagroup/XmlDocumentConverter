﻿using CommandLine;

namespace LantanaGroup.XmlHarvester
{
    [Verb("mdb", HelpText = "Convert XML files into an MS Access DB")]
    internal class MDBOptions
    {
        [Option('c', "config", Required = true, HelpText = "The location of the mapping config XML file.")]
        public string MappingConfig { get; set; }

        [Option('i', "input", Required = true, HelpText = "The directory that contains the input XML files.")]
        public string InputDirectory { get; set; }

        [Option('o', "output", Required = true, HelpText = "The directory where output (XLSX and MDB) files should go.")]
        public string OutputDirectory { get; set; }

        [Option('m', "move", Required = false, HelpText = "The directory to move input files to once they are done being processed.")]
        public string MoveDirectory { get; set; }

        [Option('x', "xsd", Required = false, HelpText = "The path to an XML Schema (XSD) that should be used to validate the structure of each XMl document processed.")]
        public string SchemaPath { get; set; }

        [Option('s', "sch", Required = false, HelpText = "The path to an ISO Schematron (SCH) file that should be used to validate the content of each XMl document processed.")]
        public string SchematronPath { get; set; }
    }
}
