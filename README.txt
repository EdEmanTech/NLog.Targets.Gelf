NLog.Targets.Gelf is a custom target for NLog version 2.0 and Graylog2 server version 0.9.5.

To use NLog.Targets.Gelf just add the following to your config file and place NLog.Targets.Gelf.dll in the same location as the NLog.dll file:

<nlog>
	<extensions>

		<add assembly="NLog.Targets.Gelf" />
	</extensions>

	<targets>
		<target name="Gelf" type="Gelf" gelfserver="" port="12201" maxchunksize="8154" />
	</targets>
	<rules>
		<logger name="*" minLevel="Trace" appendTo="Gelf"/>
	</rules>
</nlog>

Just remember to add in your server URL (without http://) or IP address.

This project was an amalgamation of NLog.Targets.Syslog (https://github.com/graffen/NLog.Targets.Syslog) and Gelf4Net (https://github.com/jjchiw/gelf4net).  Thanks to those guys for their code.