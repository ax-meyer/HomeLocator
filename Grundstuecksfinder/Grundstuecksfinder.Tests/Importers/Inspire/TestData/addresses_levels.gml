<?xml version="1.0" encoding="UTF-8"?>
<!-- Level-hierarchy address data without AGS codes, modeled on real BW and HE WFS responses (2026-09-21). BW misspells its own Land; HE has no PostalDescriptor and its server replaces every non-ASCII character with U+FFFD. -->
<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0"
                       xmlns:gml="http://www.opengis.net/gml/3.2"
                       xmlns:xlink="http://www.w3.org/1999/xlink"
                       xmlns:gn="http://inspire.ec.europa.eu/schemas/gn/4.0"
                       xmlns="http://inspire.ec.europa.eu/schemas/ad/4.0">
  <wfs:member>
    <Address gml:id="Address_BW_1">
      <position><GeographicPosition><geometry>
        <gml:Point gml:id="Address_BW_1_pos" srsName="urn:ogc:def:crs:EPSG::25832"><gml:pos>470000.1 5445000.2</gml:pos></gml:Point>
      </geometry></GeographicPosition></position>
      <locator><AddressLocator><designator><LocatorDesignator>
        <designator>7</designator>
        <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
      </LocatorDesignator></designator></AddressLocator></locator>
      <component xlink:href="#AdminUnitName_bw_land"/>
      <component xlink:href="#AdminUnitName_bw_rb"/>
      <component xlink:href="#AdminUnitName_bw_kreis"/>
      <component xlink:href="#AdminUnitName_bw_vvg"/>
      <component xlink:href="#AdminUnitName_bw_gem"/>
      <component xlink:href="#ThoroughfareName_BW_1"/>
      <component xlink:href="#PostalDescriptor_BW_1"/>
    </Address>
  </wfs:member>
  <wfs:member>
    <Address gml:id="Address_HE_1">
      <position><GeographicPosition><geometry>
        <gml:Point gml:id="Address_HE_1_pos" srsName="urn:ogc:def:crs:EPSG::25832"><gml:pos>475500.1 5550500.2</gml:pos></gml:Point>
      </geometry></GeographicPosition></position>
      <locator><AddressLocator><designator><LocatorDesignator>
        <designator>1</designator>
        <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
      </LocatorDesignator></designator></AddressLocator></locator>
      <component xlink:href="#AdminUnitName_he_rb"/>
      <component xlink:href="#AdminUnitName_he_kreis"/>
      <component xlink:href="#AdminUnitName_he_land"/>
      <component xlink:href="#AdminUnitName_he_gem"/>
      <component xlink:href="#ThoroughfareName_HE_1"/>
    </Address>
  </wfs:member>
  <wfs:additionalObjects>
    <wfs:SimpleFeatureCollection>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bw_land"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Baden-Würtemberg</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/2ndOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bw_rb"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Karlsruhe</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/3rdOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bw_kreis"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Karlsruhe</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/4thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bw_vvg"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>VVG der Stadt Bruchsal</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/5thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_bw_gem"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Bruchsal</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/6thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><ThoroughfareName gml:id="ThoroughfareName_BW_1"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Kaiserstraße</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name></ThoroughfareName></wfs:member>
      <wfs:member><PostalDescriptor gml:id="PostalDescriptor_BW_1"><postName><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Bruchsal</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></postName><postCode>76646</postCode></PostalDescriptor></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_he_rb"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Darmstadt</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/3rdOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_he_kreis"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Kreisfreie Stadt Frankfurt am Main</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/4thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_he_land"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Hessen</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/2ndOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_he_gem"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Frankfurt am Main</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/6thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><ThoroughfareName gml:id="ThoroughfareName_HE_1"><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Adam-Riese-Stra�e</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name></ThoroughfareName></wfs:member>
    </wfs:SimpleFeatureCollection>
  </wfs:additionalObjects>
</wfs:FeatureCollection>
