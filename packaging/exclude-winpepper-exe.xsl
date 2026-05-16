<?xml version="1.0" encoding="UTF-8"?>
<!--
  Heat XSL transform: strip the auto-generated Component for Winpepper.exe out of the
  harvested ComponentGroup so it does not collide with the hand-authored
  WinpepperExeAlias component in winpepper.wxs (which gives the file a stable
  bind id of "WinpepperExe" for use by [#WinpepperExe] and
  !(bind.FileVersion.WinpepperExe)). Heat does not expose a CLI -x/exclude flag;
  the WiX-blessed pattern for excluding a single file is an XSL transform.
-->
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:wix="http://wixtoolset.org/schemas/v4/wxs"
                exclude-result-prefixes="wix">

  <xsl:output method="xml" indent="yes" />

  <!-- Identity template: copy everything by default. -->
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <!-- Capture the set of Component ids whose File @Source ends with "\Winpepper.exe". -->
  <xsl:key name="winpepperExeComponents"
           match="wix:Component[wix:File[
             substring(@Source, string-length(@Source) - 13) = '\Winpepper.exe']]"
           use="@Id" />

  <!-- Drop those components from the harvested DirectoryRef fragment. -->
  <xsl:template match="wix:Component[
    wix:File[substring(@Source, string-length(@Source) - 13) = '\Winpepper.exe']]" />

  <!-- Drop the matching ComponentRef from any ComponentGroup. -->
  <xsl:template match="wix:ComponentRef[key('winpepperExeComponents', @Id)]" />

</xsl:stylesheet>
