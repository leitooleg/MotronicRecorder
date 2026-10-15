using System;
using System.IO;
using System.Reflection;

namespace MotronicRecorder
{
    internal static class ProductInfo
    {
        public const string Version = "0.2.4";
        public const string Name = "Motronic Recorder by leitoauto.com";
        public const string Website = "https://leitoauto.com";
        public const string AuthorName = "Автор: Рябцев Олег.";
        public const string Author = "Программа создана энтузиастом-специалистом по компьютерным сетям.";
        public const string Disclaimer =
            "Программа предоставляется «как есть» (AS IS), без каких-либо явных или подразумеваемых гарантий, " +
            "включая гарантии работоспособности, безопасности и пригодности для конкретной цели.\r\n\r\n" +
            "Использование программы и подключение диагностического оборудования выполняются на ваш риск. " +
            "В пределах, допускаемых применимым законодательством, автор не несёт ответственности за " +
            "повреждённые ЭБУ, сгоревшие датчики, короткие замыкания и повреждение проводки, двигателя " +
            "и другого оборудования, потерю данных и любые иные убытки, связанные с использованием программы.\r\n\r\n" +
            "Программа отправляет только предусмотренные запросы чтения и служебные подтверждения. " +
            "Отсутствие команд записи не гарантирует отсутствие влияния диагностического обмена на ЭБУ. " +
            "Автономные проверки кода и имитатора не заменяют проверку конкретного автомобиля, адаптера и проводки.";

        public static string AboutText
        {
            get { return AuthorName + "\r\n\r\n" + Author + "\r\n\r\nПроверено на Opel Vectra A, ECU Bosch Motronic 1.5.\r\n\r\n" + Disclaimer; }
        }

        public static string ConnectionGuide()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MotronicRecorder.ConnectionGuide.txt"))
            {
                if (stream == null) return "Инструкция отсутствует в этой сборке. См. файл ПОДКЛЮЧЕНИЕ.txt из архива выпуска.";
                using (StreamReader reader = new StreamReader(stream))
                    return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            }
        }
    }
}
