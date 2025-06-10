using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Tedd.ZeroAllocationLogger;
public static class EndPointFormatter
{
    /// <summary>
    /// Formats the EndPoint (IPEndPoint or DnsEndPoint) as ASCII bytes into the provided span.
    /// Returns the number of bytes written. Throws on insufficient buffer.
    /// </summary>
    /// <param name="endPoint">EndPoint object</param>
    /// <param name="buffer">Buffer large enough to hold endpoint data, i.e. 64 bytes</param>
    /// <returns>Number of bytes in buffer that was used</returns>
    public static int WriteEndPointAscii(EndPoint endPoint, Span<byte> buffer)
    {
        if (endPoint is IPEndPoint ip)
        {
            int written = 0;
            // Handle IPv6 with brackets
            if (ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                buffer[written++] = (byte)'[';
                if (!ip.Address.TryFormat(buffer.Slice(written), out int ipBytes))
                    throw new ArgumentException("Buffer too small for IP address.");
                written += ipBytes;
                buffer[written++] = (byte)']';
                buffer[written++] = (byte)':';
            }
            else
            {
                // IPv4: no brackets
                if (!ip.Address.TryFormat(buffer, out int ipBytes))
                    throw new ArgumentException("Buffer too small for IP address.");
                written += ipBytes;
                buffer[written++] = (byte)':';
            }

            if (!ip.Port.TryFormat(buffer.Slice(written), out int portBytes, provider: null))
                throw new ArgumentException("Buffer too small for port.");
            written += portBytes;
            return written;
        }
        else if (endPoint is DnsEndPoint dns)
        {
            int written = 0;
            // Hostname
            var host = dns.Host;
            if (host.Length > buffer.Length) throw new ArgumentException("Buffer too small for hostname.");
            for (int i = 0; i < host.Length; i++) buffer[written++] = (byte)host[i];
            buffer[written++] = (byte)':';
            if (!dns.Port.TryFormat(buffer.Slice(written), out int portBytes, provider: null))
                throw new ArgumentException("Buffer too small for port.");
            written += portBytes;
            return written;
        }
        else
        {
            throw new NotSupportedException("Only IPEndPoint and DnsEndPoint are supported.");
        }
    }
}
