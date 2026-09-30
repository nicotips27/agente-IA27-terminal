namespace ECnet;

public static class Banner
{
    public static void Render(AppConfig config, string modelName)
    {
        if (!config.ShowBanner)
        {
            Console.WriteLine("IA27 Terminal: escribi /help para ver los comandos.");
            Console.WriteLine();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("//============================== IR3C5.CORE ==============================//");
        Console.WriteLine("                 ####");
        Console.WriteLine("        ##       ####       ##");
        Console.WriteLine("         ####    #####    ####");
        Console.WriteLine("           #### ##### ####");
        Console.WriteLine("             ##########");
        Console.WriteLine("    ##        ##########        ##");
        Console.WriteLine("      ####   ###########   ####");
        Console.WriteLine("        ###################");
        Console.WriteLine("   ################################");
        Console.WriteLine("        ###################");
        Console.WriteLine("      ####   ###########   ####");
        Console.WriteLine("    ##        ##########        ##");
        Console.WriteLine("             ##########");
        Console.WriteLine("           #### ##### ####");
        Console.WriteLine("         ####    #####    ####");
        Console.WriteLine("        ##       ####       ##");
        Console.WriteLine("                 ####");
        Console.WriteLine("//============================== ESTALINGRADO CORP ======================//");
        Console.WriteLine("  INTRA-NET :: IA27 TERMINAL :: sistema local // canal seguro");
        Console.WriteLine("  ------------------------------------------------------------------------");
        Console.WriteLine("  modelo  : " + modelName);
        Console.WriteLine("  origen  : " + config.ModelDirectory);
        Console.WriteLine("  motor   : ECnet local // net: " + (config.NetEnabled ? "on" : "off") + " // internet bajo autorización");
        Console.WriteLine("  ------------------------------------------------------------------------");
        Console.WriteLine("  /help ayuda · /clear limpiar · /use <modelo> cambiar · /harness <misión> · /exit salir");
        Console.WriteLine();
        Console.WriteLine("Cargando el modelo; la primera carga puede tardar...");
        Console.WriteLine("Listo. Escribe /help para ver los comandos del agente. Ctrl+C detiene la respuesta en curso.");
        Console.WriteLine();
        Console.ResetColor();
    }
}
