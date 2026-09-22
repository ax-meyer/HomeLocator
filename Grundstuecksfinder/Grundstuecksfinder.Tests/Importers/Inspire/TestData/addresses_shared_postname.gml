<?xml version="1.0" encoding="UTF-8"?>
<!-- Modeled on a real SH WFS response (2026-09-22): the PostalDescriptor is one object per postcode, and its postName ("Petersdorf a. F.") is one village of the Gemeinde Fehmarn rather than the address's own town. -->
<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0"
                       xmlns:gml="http://www.opengis.net/gml/3.2"
                       xmlns:xlink="http://www.w3.org/1999/xlink"
                       xmlns:gn="http://inspire.ec.europa.eu/schemas/gn/4.0"
                       xmlns="http://inspire.ec.europa.eu/schemas/ad/4.0">
  <wfs:member>
    <Address gml:id="Address_DESHPDHK0004CcJD">
      <position><GeographicPosition><geometry>
        <gml:Point gml:id="Address_DESHPDHK0004CcJD_pos" srsName="http://www.opengis.net/def/crs/epsg/0/25832"><gml:pos>643030.362 6033977.173</gml:pos></gml:Point>
      </geometry></GeographicPosition></position>
      <locator><AddressLocator><designator><LocatorDesignator>
        <designator>24</designator>
        <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
      </LocatorDesignator></designator></AddressLocator></locator>
      <component xlink:href="#AdminUnitName_01055046"/>
      <component xlink:href="#ThoroughfareName_0105504606054"/>
      <component xlink:href="#AdminUnitName_01055"/>
      <component xlink:href="#AdminUnitName_01"/>
      <component xlink:href="#PostalDescriptor_23769"/>
    </Address>
  </wfs:member>
  <wfs:additionalObjects>
    <wfs:SimpleFeatureCollection>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_01"><alternativeIdentifier>01</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Schleswig-Holstein</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/2ndOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_01055"><alternativeIdentifier>01055</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Ostholstein</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/4thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><AdminUnitName gml:id="AdminUnitName_01055046"><alternativeIdentifier>01055046</alternativeIdentifier><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Fehmarn</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name><level xlink:href="http://inspire.ec.europa.eu/codelist/AdministrativeHierarchyLevel/6thOrder"/></AdminUnitName></wfs:member>
      <wfs:member><ThoroughfareName gml:id="ThoroughfareName_0105504606054"><name><ThoroughfareNameValue><name><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Breite Straße</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></name></ThoroughfareNameValue></name></ThoroughfareName></wfs:member>
      <wfs:member><PostalDescriptor gml:id="PostalDescriptor_23769"><postName><gn:GeographicalName><gn:spelling><gn:SpellingOfName><gn:text>Petersdorf a. F.</gn:text></gn:SpellingOfName></gn:spelling></gn:GeographicalName></postName><postCode>23769</postCode></PostalDescriptor></wfs:member>
    </wfs:SimpleFeatureCollection>
  </wfs:additionalObjects>
</wfs:FeatureCollection>
