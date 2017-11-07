﻿using EdiEngine.Common.Definitions;
using EdiEngine.Runtime;
using System;
using System.IO;

namespace EdiEngine
{
    public class EdiDataReader
    {
        private const string MissingStandardFallbackVersion = "004010";

        private string _elementSeparator;
        private string _segmentSeparator;

        public void FromStream(Stream fileContent, EdiBatch batch)
        {
            string stringContent;
            using (StreamReader sr = new StreamReader(fileContent))
            {
                stringContent = sr.ReadToEnd();
            }

            FromString(stringContent, batch);
        }

        public void FromString(string fileContent, EdiBatch batch)
        {
            if (string.IsNullOrWhiteSpace(fileContent))
                throw new EdiParsingException("Empty File");

            if (!fileContent.StartsWith("ISA"))
                throw new EdiParsingException("ISA NOT FOUND");

            _elementSeparator = fileContent[3].ToString();
            _segmentSeparator = fileContent.Substring(105, fileContent.IndexOf($"GS{_elementSeparator}", StringComparison.Ordinal) - 105);

            string[] segments = fileContent.Split(new [] { _segmentSeparator }, StringSplitOptions.RemoveEmptyEntries);

            EdiInterchange currentInterchange = null;
            EdiGroup currentGroup = null;
            EdiTrans currentTrans = null;
            EdiMapReader mapReader = null;

            int tranSegCount = 0;
            int groupsCount = 0;
            int transCount = 0;

            foreach (string seg in segments)
            {
                string[] elements = seg.Split(new [] { _elementSeparator }, StringSplitOptions.None);
                MapSegment segDef;
                switch (elements[0])
                {
                    case "ISA":
                        groupsCount = 0;
                        currentInterchange = new EdiInterchange
                        {
                            SegmentSeparator = _segmentSeparator,
                            ElementSeparator = _elementSeparator
                        };

                        segDef = GetSegDefinition("ISA", $"{elements[12]}0", MissingStandardFallbackVersion);
                        currentInterchange.ISA = MapReader.ProcessSegment(segDef, elements, 0, currentInterchange);
                        break;

                    case "IEA":
                        if (currentInterchange == null)
                            throw new EdiParsingException("MALFORMED  DATA");

                        segDef = GetSegDefinition("IEA", $"{currentInterchange.ISA.Content[11].Val}0", MissingStandardFallbackVersion);
                        currentInterchange.IEA = MapReader.ProcessSegment(segDef, elements, 0, currentInterchange);
                        batch.Interchanges.Add(currentInterchange);

                        int declaredGroupCount;
                        int.TryParse(elements[1], out declaredGroupCount);

                        if (declaredGroupCount != groupsCount)
                        {
                            AddValidationError(currentInterchange, $"Expected {declaredGroupCount} groups. Found {groupsCount}. Interchange # {elements[2]}.");
                        }

                        if (!CheckControlNumbersAreEgual(currentInterchange.ISA.Content[12].Val, elements[2]))
                        {
                            AddValidationError(currentInterchange, $"Control numbers do not match. ISA {currentInterchange.ISA.Content[12].Val}. IEA {elements[2]}.");
                        }

                        currentInterchange = null;
                        break;

                    case "GS":
                        groupsCount++;
                        transCount = 0;
                        currentGroup = new EdiGroup(elements[1]);

                        segDef = GetSegDefinition("GS", $"{elements[8]}", MissingStandardFallbackVersion);
                        currentGroup.GS = MapReader.ProcessSegment(segDef, elements, 0, currentGroup);
                        break;

                    case "GE":
                        if (currentInterchange == null || currentGroup == null)
                            throw new EdiParsingException("MALFORMED DATA");

                        segDef = GetSegDefinition("GE", $"{currentGroup.GS.Content[7].Val}", MissingStandardFallbackVersion);
                        currentGroup.GE = MapReader.ProcessSegment(segDef, elements, 0, currentGroup);
                        currentInterchange.Groups.Add(currentGroup);

                        int declaredTransCount;
                        int.TryParse(elements[1], out declaredTransCount);

                        if (declaredTransCount != transCount)
                        {
                            AddValidationError(currentGroup, $"Expected {declaredTransCount} transactions. Found {transCount}. Group # {elements[2]}.");
                        }

                        if (!CheckControlNumbersAreEgual(currentGroup.GS.Content[5].Val, elements[2]))
                        {
                            AddValidationError(currentGroup, $"Control numbers do not match. GS {currentGroup.GS.Content[5].Val}. GE {elements[2]}.");
                        }

                        currentGroup = null;
                        break;

                    case "ST":
                        if (currentInterchange == null || currentGroup == null)
                            throw new EdiParsingException("MALFORMED DATA");

                        string asmName = $"EdiEngine.Standards.X12_{currentGroup.GS.Content[7].Val}";
                        string typeName = $"{asmName}.M_{elements[1]}";

                        var map = Activator.CreateInstance(asmName, typeName).Unwrap();
                        if (!(map is MapLoop))
                        {
                            AddValidationError(currentTrans, $"Can not find map {elements[1]} for standard {currentGroup.GS.Content[7].Val}. Skipping Transaction.");
                            break;
                        }

                        transCount++;
                        currentTrans = new EdiTrans((MapBaseEntity)map);

                        segDef = GetSegDefinition("ST", $"{currentGroup.GS.Content[7].Val}", MissingStandardFallbackVersion);
                        currentTrans.ST = MapReader.ProcessSegment(segDef, elements, 0, currentTrans);

                        tranSegCount = 1;

                        mapReader = new EdiMapReader((MapLoop)map, currentTrans);
                        break;

                    case "SE":
                        if (currentInterchange == null || currentGroup == null || currentTrans == null)
                            throw new EdiParsingException("MALFORMED DATA");

                        tranSegCount++;

                        int declaredSegCount;
                        int.TryParse(elements[1], out declaredSegCount);

                        if (declaredSegCount != tranSegCount)
                        {
                            AddValidationError(currentTrans, $"Expected {declaredSegCount} segments. Found {tranSegCount}. Trans # {elements[2]}.");
                        }

                        if (!CheckControlNumbersAreEgual(currentTrans.ST.Content[1].Val, elements[2]))
                        {
                            AddValidationError(currentTrans, $"Control numbers do not match. ST {currentTrans.ST.Content[1].Val}. SE {elements[2]}.");
                        }

                        segDef = GetSegDefinition("SE", $"{currentGroup.GS.Content[7].Val}", MissingStandardFallbackVersion);
                        currentTrans.SE = MapReader.ProcessSegment(segDef, elements, 0, currentTrans);

                        currentGroup.Transactions.Add(currentTrans);
                        currentTrans = null;
                        break;

                    default:
                        tranSegCount++;
                        mapReader?.ProcessRawSegment(elements[0], elements, tranSegCount);
                        break;
                }
            }
        }

        private MapSegment GetSegDefinition(string segName, string version, string fallBackVersion)
        {
            string asmName = $"EdiEngine.Standards.X12_{version}";
            string typeName = $"{asmName}.Segments.{segName}";
            try
            {
                return (MapSegment)Activator.CreateInstance(asmName, typeName).Unwrap();
            }
            catch (FileNotFoundException) { }

            //failed to create main version - going to fallback
            if (!string.IsNullOrWhiteSpace(fallBackVersion))
            {
                return GetSegDefinition(segName, fallBackVersion, null);
            }

            throw new InvalidOperationException($"Unable to find {segName} in definitions for {version}.");
        }

        private void AddValidationError(IValidatedEntity obj, string message)
        {
            ValidationError err = new ValidationError()
            {
                Message = message
            };

            obj?.ValidationErrors.Add(err);
        }

        private bool CheckControlNumbersAreEgual(string headerIcn, string footerIcn)
        {
            int icn1, icn2;

            var res = int.TryParse(headerIcn, out icn1);
            res = int.TryParse(footerIcn, out icn2) & res;

            return res && icn1 == icn2;
        }
    }
}
