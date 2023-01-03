namespace Sequin.Rtmp.Amf;

/// <summary>
/// AMF 0 Data Type Marker Definitions
/// </summary>
/// <see cref="http://download.macromedia.com/pub/labs/amf/amf0_spec_121207.pdf"/>
internal enum Amf0TypeMarker : byte
{
    Number      = 0x00,
    Boolean     = 0x01,
    String      = 0x02,
    Object      = 0x03,
    Movieclip   = 0x04, // reserved, not supported 
    Null        = 0x05,
    Undefined   = 0x06,
    Reference   = 0x07,
    EcmaArray   = 0x08,
    ObjectEnd   = 0x09,
    StrictArray = 0x0A,
    Date        = 0x0B,
    LongString  = 0x0C,
    Unsupported = 0x0D,
    Recordset   = 0x0E, // reserved, not supported 
    XmlDocument = 0x0F,
    TypedObject = 0x10,
    
    AvmplusObject = 0x11,
}

internal static class Amf0TypeMarkerExtension
{
    public static int GetNextTokenLength(this Amf0TypeMarker marker)
    {
        return marker switch
        {
            // Next to VALUE itself
            Amf0TypeMarker.Number    => 4,
            Amf0TypeMarker.Boolean   => 1,
            Amf0TypeMarker.Reference => 2,

            // Next to additional LENGTH field
            Amf0TypeMarker.String      => 2,
            Amf0TypeMarker.LongString  => 4,
            Amf0TypeMarker.XmlDocument => 4,
            
            // No value
            Amf0TypeMarker.Null      => 0,
            Amf0TypeMarker.Undefined => 0,

            // Cannot identify length at this time, or is used as a part of another data type
            _ => -1,
        };

    }
}
