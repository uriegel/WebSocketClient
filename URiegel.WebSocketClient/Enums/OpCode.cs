namespace URiegel.WebSocketClient.Enums;

enum OpCode : byte
{
    /// <summary>
    /// Diese Nachricht muss an die vorherige angehängt werden. Wenn der fin-Wert 0 ist, folgen weitere Fragmente, 
    /// bei fin=1 ist die Nachricht komplett verarbeitet.
    /// </summary>
    ContinuationFrame = 0,
    Text,
    Binary,
    Close = 8,
    /// <summary>
    /// Ping erhalten, direkt einen Pong zurücksenden mit denselben payload-Daten
    /// </summary>
    Ping,
    /// <summary>
    /// Wird serverseitig ignoriert
    /// </summary>
    Pong
}
